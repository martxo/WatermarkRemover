#!/usr/bin/env python3
"""WatermarkRemover 数据端点。

1) POST /push          接收备份报表 JSON，写 web/data/<machine>.json
2) POST /credit/upload 接收征信整理 SQLite 数据库（二进制），原子替换 web/credit.db
3) GET  /credit        分页查询征信整理数据库

监听 127.0.0.1:8787，仅经 nginx 反代可达。
systemd 单元：wm-backup-stats.service
"""
import ipaddress
import json
import os
import sqlite3
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

ROOT = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.join(ROOT, 'web')
DATA_DIR = os.path.join(WEB, 'data')
CREDIT_DB = os.path.join(WEB, 'credit.db')
TOKEN = 'wmbs-c850bb13004d426667a02d65bf824e0e'
PORT = 8787
MAX_BODY = 2_000_000
MAX_DB = 32_000_000


def atomic_write(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        f.write(text)
    os.replace(tmp, path)


def atomic_write_bytes(path: str, data: bytes) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, path)


GEO_CACHE = {}


def client_ip(handler) -> str:
    """取真实客户端 IP：优先 nginx 的 X-Real-IP。"""
    for h in ('X-Real-IP', 'X-Forwarded-For'):
        v = handler.headers.get(h)
        if v:
            return v.split(',')[0].strip()
    return handler.client_address[0]


def geo_lookup(ip: str) -> str:
    """IP 归属地查询（ip-api.com，中文），带 24 小时缓存；失败返回空串。"""
    if not ip:
        return ''
    try:
        if ipaddress.ip_address(ip).is_private:
            return ''
    except ValueError:
        return ''
    now = time.time()
    hit = GEO_CACHE.get(ip)
    if hit and now - hit[1] < 86400:
        return hit[0]
    loc = ''
    try:
        url = 'http://ip-api.com/json/%s?lang=zh-CN&fields=country,regionName,city,isp' % ip
        with urllib.request.urlopen(url, timeout=2) as r:
            d = json.loads(r.read().decode('utf-8'))
        loc = ' '.join(x for x in (d.get('country'), d.get('regionName'),
                                   d.get('city'), d.get('isp')) if x)
    except Exception:
        loc = ''
    GEO_CACHE[ip] = [loc, now]
    return loc


def query_credit(qs):
    page = max(1, int((qs.get('page') or ['1'])[0]))
    size = min(200, max(10, int((qs.get('size') or ['50'])[0])))
    q = (qs.get('q') or [''])[0].strip()
    status = (qs.get('status') or [''])[0].strip()

    if not os.path.exists(CREDIT_DB):
        return {'ok': True, 'total': 0, 'page': page, 'size': size,
                'stats': {}, 'rows': []}

    conn = sqlite3.connect('file:%s?mode=ro' % CREDIT_DB, uri=True, timeout=5)
    conn.row_factory = sqlite3.Row
    try:
        where, args = [], []
        if q:
            where.append('(person LIKE ? OR new_name LIKE ? OR src_path LIKE ? OR note LIKE ?)')
            args += ['%' + q + '%'] * 4
        if status:
            where.append('status = ?')
            args.append(status)
        w = ('WHERE ' + ' AND '.join(where)) if where else ''
        total = conn.execute('SELECT COUNT(*) FROM files ' + w, args).fetchone()[0]
        rows = conn.execute(
            'SELECT person, birth_date, report_time, region, status, new_name, '
            'src_path, size, processed_at, note, multi FROM files ' + w +
            ' ORDER BY report_time DESC, id DESC LIMIT ? OFFSET ?',
            args + [size, (page - 1) * size]).fetchall()
        st = conn.execute(
            "SELECT COUNT(*) total, SUM(status='已整理') done, "
            "SUM(status='已跳过') skipped, "
            "SUM(note LIKE '%请人工复核%' OR note LIKE '%未能辨认%' "
            "OR note LIKE '%无法辨认%' OR note LIKE '%非报告首页%' "
            "OR note LIKE '%简版%') flagged FROM files").fetchone()
        return {'ok': True, 'total': total, 'page': page, 'size': size,
                'stats': {'total': st[0], 'done': st[1] or 0,
                          'skipped': st[2] or 0, 'flagged': st[3] or 0},
                'rows': [dict(r) for r in rows]}
    finally:
        conn.close()


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def do_POST(self):
        path = urlparse(self.path).path
        if self.headers.get('X-Push-Token') != TOKEN:
            self._json(403, {'ok': False, 'error': 'forbidden'})
            return
        try:
            length = int(self.headers.get('Content-Length', '0'))
        except ValueError:
            length = 0

        if path == '/push':
            if length <= 0 or length > MAX_BODY:
                self._json(413, {'ok': False, 'error': 'body too large'})
                return
            raw = self.rfile.read(length)
            try:
                doc = json.loads(raw.decode('utf-8'))
                machine = ''.join(
                    c for c in str(doc.get('machine', 'unknown'))
                    if c.isalnum() or c in '-_')[:64] or 'unknown'
            except Exception:
                self._json(400, {'ok': False, 'error': 'bad json'})
                return
            doc['clientIp'] = client_ip(self)
            if not doc.get('ipLocation'):
                doc['ipLocation'] = geo_lookup(doc['clientIp'])
            atomic_write(os.path.join(DATA_DIR, machine + '.json'),
                         json.dumps(doc, ensure_ascii=False))
            idx_path = os.path.join(DATA_DIR, 'index.json')
            try:
                with open(idx_path, encoding='utf-8') as f:
                    idx = json.load(f)
                if not isinstance(idx.get('machines'), dict):
                    idx = {}
            except Exception:
                idx = {}
            idx.setdefault('machines', {})[machine] = {
                'updatedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
            atomic_write(idx_path, json.dumps(idx, ensure_ascii=False))
            self._json(200, {'ok': True})
            return

        if path == '/credit/upload':
            if length <= 0 or length > MAX_DB:
                self._json(413, {'ok': False, 'error': 'body too large'})
                return
            raw = self.rfile.read(length)
            if not raw.startswith(b'SQLite format 3\x00'):
                self._json(400, {'ok': False, 'error': 'not a sqlite file'})
                return
            atomic_write_bytes(CREDIT_DB, raw)
            self._json(200, {'ok': True, 'bytes': len(raw)})
            return

        self._json(404, {'ok': False, 'error': 'not found'})

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == '/credit':
            try:
                self._json(200, query_credit(parse_qs(u.query)))
            except Exception as e:
                self._json(500, {'ok': False, 'error': str(e)[:200]})
            return
        self._json(404, {'ok': False, 'error': 'not found'})

    def _json(self, code: int, obj) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


if __name__ == '__main__':
    ThreadingHTTPServer.allow_reuse_address = True
    ThreadingHTTPServer(('127.0.0.1', PORT), Handler).serve_forever()
