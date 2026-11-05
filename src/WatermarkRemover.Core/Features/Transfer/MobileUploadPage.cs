namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// The chat page the phone sees after scanning the QR code. Dependency-free (fetch + FormData)
/// so it renders identically in the system browser, WeChat's built-in browser and iOS Safari.
/// Text and files are sent to the computer; computer-sent text/files/images appear as bubbles
/// through a 1.5 s poll. Scroll position is respected: new rows only pull the view down when the
/// user is already at the bottom.
/// </summary>
public static class MobileUploadPage
{
    public static string Html => s_html.Value;

    private static readonly Lazy<string> s_html = new(() => """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <title>办公助理 · 传文件</title>
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
                html, body { height: 100%; }
                body {
                    font-family: -apple-system, "PingFang SC", "Microsoft YaHei", sans-serif;
                    background: #ededed; color: #1f2329;
                    display: flex; flex-direction: column;
                    max-width: 560px; margin: 0 auto;
                }
                header {
                    flex: none; background: #f7f7f7; border-bottom: 1px solid #dcdcdc;
                    padding: calc(10px + env(safe-area-inset-top)) 16px 10px;
                    display: flex; align-items: center; justify-content: space-between;
                }
                header .name { font-size: 17px; font-weight: 600; }
                header .sub { display: block; font-size: 11px; font-weight: 400; color: #8a919f; margin-top: 2px; }
                #dot { width: 8px; height: 8px; border-radius: 50%; background: #c8412b; }
                #dot.ok { background: #1aad19; }
                #chat {
                    flex: 1; overflow-y: auto; padding: 14px 14px 6px;
                    -webkit-overflow-scrolling: touch;
                }
                #empty { color: #9aa0a6; font-size: 13px; line-height: 1.8; text-align: center; margin-top: 32px; }
                .row { display: flex; margin-bottom: 12px; align-items: flex-start; }
                .row.mine { justify-content: flex-end; }
                .bubble {
                    max-width: 78%; border-radius: 10px; padding: 9px 11px;
                    font-size: 14px; line-height: 1.5; word-break: break-word;
                    background: #ffffff; box-shadow: 0 1px 2px rgba(0,0,0,.05);
                }
                .row.mine .bubble { background: #95ec69; }
                .bubble .time { display: block; font-size: 10px; color: #8a919f; margin-top: 4px; }
                .bubble img { display: block; max-width: 210px; max-height: 260px; border-radius: 8px; margin-bottom: 4px; background: #f2f2f2; }
                .filecard { display: flex; align-items: center; gap: 10px; min-width: 170px; }
                .fileicon {
                    width: 34px; height: 34px; border-radius: 6px; background: #e8f0fe;
                    color: #2f5fe0; font-size: 15px; font-weight: 700;
                    display: flex; align-items: center; justify-content: center; flex: none;
                }
                .filemeta { flex: 1; min-width: 0; }
                .filename { font-size: 13px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
                .filesize { font-size: 11px; color: #8a919f; margin-top: 2px; }
                .dl { flex: none; font-size: 12px; color: #2f5fe0; text-decoration: none; padding: 4px 2px; }
                #newChip {
                    position: fixed; bottom: 84px; left: 50%; transform: translateX(-50%);
                    background: rgba(31,35,41,.88); color: #fff; font-size: 12px;
                    border-radius: 14px; padding: 6px 14px; border: 0;
                }
                footer {
                    flex: none; background: #f7f7f7; border-top: 1px solid #dcdcdc;
                    padding: 8px 10px calc(8px + env(safe-area-inset-bottom));
                    display: flex; align-items: center; gap: 8px;
                }
                #fileBtn {
                    flex: none; width: 38px; height: 38px; border-radius: 50%; border: 1px solid #d5d5d5;
                    background: #fff; color: #333; font-size: 22px; line-height: 1; cursor: pointer;
                }
                #msg {
                    flex: 1; min-width: 0; height: 38px; border: 0; border-radius: 8px;
                    background: #fff; padding: 0 12px; font-size: 15px; font-family: inherit; color: #1f2329;
                }
                #msg:focus { outline: 1px solid #c7d7f7; }
                #send {
                    flex: none; height: 38px; padding: 0 16px; border: 0; border-radius: 8px;
                    background: #07c160; color: #fff; font-size: 14px; font-weight: 600; cursor: pointer;
                }
                #send:disabled { background: #c4e8d3; }
                #toast {
                    position: fixed; left: 50%; bottom: 96px; transform: translateX(-50%) translateY(10px);
                    background: rgba(31,35,41,.92); color: #fff; font-size: 13px; padding: 9px 16px;
                    border-radius: 8px; opacity: 0; pointer-events: none; transition: all .2s; max-width: 86vw;
                }
                #toast.show { opacity: 1; transform: translateX(-50%) translateY(0); }
            </style>
            </head>
            <body>
            <header>
                <div>
                    <div class="name">办公助理</div>
                    <span class="sub">局域网直传 · 不经过任何服务器</span>
                </div>
                <div id="dot" title="连接状态"></div>
            </header>

            <main id="chat">
                <div id="empty">这里会显示电脑传过来的文字、图片和文件。<br>发消息或选择文件即可传到电脑。</div>
            </main>

            <button id="newChip" type="button" hidden>有新消息 ↓</button>

            <footer>
                <button id="fileBtn" type="button" title="发送文件或图片">+</button>
                <input id="msg" type="text" placeholder="发消息…" autocomplete="off">
                <button id="send" type="button" disabled>发送</button>
            </footer>
            <div id="toast"></div>
            <input id="file" type="file" multiple hidden>

            <script>
                var token = new URLSearchParams(location.search).get('t') || '';
                var chat    = document.getElementById('chat'),
                    empty   = document.getElementById('empty'),
                    msg     = document.getElementById('msg'),
                    sendBtn = document.getElementById('send'),
                    fileBtn = document.getElementById('fileBtn'),
                    fileIn  = document.getElementById('file'),
                    dot     = document.getElementById('dot'),
                    chip    = document.getElementById('newChip'),
                    toast   = document.getElementById('toast');

                var lastId = 0, atBottom = true, polling = false, timer = null;
                var chatUrl     = '/chat?t=' + encodeURIComponent(token);
                var textUrl     = '/text?t=' + encodeURIComponent(token);
                var uploadUrl   = '/up?t=' + encodeURIComponent(token);
                var downloadUrl = '/download/';

                function toastMsg(text) {
                    toast.textContent = text;
                    toast.classList.add('show');
                    clearTimeout(toastMsg.t);
                    toastMsg.t = setTimeout(function () { toast.classList.remove('show'); }, 2600);
                }
                function human(bytes) {
                    if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(2) + ' GB';
                    if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + ' MB';
                    if (bytes >= 1024) return Math.round(bytes / 1024) + ' KB';
                    return (bytes || 0) + ' B';
                }
                function nearBottom() {
                    return chat.scrollHeight - chat.scrollTop - chat.clientHeight < 48;
                }
                function toBottom() {
                    chat.scrollTop = chat.scrollHeight;
                    atBottom = true;
                    chip.hidden = true;
                }
                chat.onscroll = function () {
                    atBottom = nearBottom();
                    if (atBottom) chip.hidden = true;
                };
                chip.onclick = toBottom;

                function makeRow(m) {
                    var row = document.createElement('div');
                    row.className = 'row ' + (m.dir === 'phone' ? 'mine' : 'theirs');
                    var bubble = document.createElement('div');
                    bubble.className = 'bubble';

                    if (m.kind === 'text') {
                        bubble.appendChild(document.createTextNode(m.text));
                    } else if (m.kind === 'image') {
                        var img = new Image();
                        img.src = downloadUrl + m.id + '?t=' + encodeURIComponent(token);
                        img.onclick = function () { window.open(img.src, '_blank'); };
                        bubble.appendChild(img);
                        var cap = document.createElement('span');
                        cap.style.fontSize = '11px';
                        cap.style.color = '#6b7280';
                        cap.textContent = m.name + ' · ' + human(m.size);
                        bubble.appendChild(cap);
                    } else {
                        var card = document.createElement('div');
                        card.className = 'filecard';
                        var icon = document.createElement('div');
                        icon.className = 'fileicon';
                        icon.textContent = 'FILE';
                        icon.style.fontSize = '9px';
                        var meta = document.createElement('div');
                        meta.className = 'filemeta';
                        var name = document.createElement('div');
                        name.className = 'filename';
                        name.textContent = m.name;
                        var size = document.createElement('div');
                        size.className = 'filesize';
                        size.textContent = human(m.size);
                        meta.appendChild(name); meta.appendChild(size);
                        var dl = document.createElement('a');
                        dl.className = 'dl';
                        dl.href = downloadUrl + m.id + '?t=' + encodeURIComponent(token);
                        dl.setAttribute('download', m.name);
                        dl.textContent = '下载';
                        card.appendChild(icon); card.appendChild(meta); card.appendChild(dl);
                        bubble.appendChild(card);
                    }

                    var time = document.createElement('span');
                    time.className = 'time';
                    time.textContent = (m.dir === 'phone' ? '我 · ' : '电脑 · ') + (m.time || '');
                    bubble.appendChild(time);
                    row.appendChild(bubble);
                    return row;
                }

                function append(rows, initial) {
                    if (!rows.length) return false;
                    empty.hidden = true;
                    var frag = document.createDocumentFragment();
                    rows.forEach(function (m) {
                        if (m.id > lastId) lastId = m.id;
                        frag.appendChild(makeRow(m));
                    });
                    var wasAtBottom = atBottom;
                    chat.appendChild(frag);
                    if (initial || wasAtBottom) toBottom();
                    else chip.hidden = false;
                    return true;
                }

                function poll() {
                    if (polling) return;
                    polling = true;
                    fetch(chatUrl + '&after=' + lastId, { cache: 'no-store' })
                        .then(function (r) { if (!r.ok) throw new Error('bad status'); return r.json(); })
                        .then(function (data) {
                            dot.className = 'ok';
                            append((data && data.messages) || []);
                        })
                        .catch(function () { dot.className = ''; })
                        .then(function () { polling = false; });
                }

                function sendText() {
                    var text = msg.value.trim();
                    if (!text) return;
                    sendBtn.disabled = true;
                    fetch(textUrl, {
                        method: 'POST',
                        headers: { 'Content-Type': 'text/plain; charset=utf-8' },
                        body: text
                    }).then(function (r) {
                        if (!r.ok) throw new Error('send failed');
                        msg.value = '';
                        poll();
                    }).catch(function () { toastMsg('发送失败，请检查连接'); })
                      .then(function () { sendBtn.disabled = !msg.value.trim(); });
                }

                function upload(files) {
                    if (!files || !files.length) return;
                    var form = new FormData();
                    for (var i = 0; i < files.length; i++) form.append('file', files[i], files[i].name);
                    var xhr = new XMLHttpRequest();
                    xhr.open('POST', uploadUrl);
                    var total = 0;
                    for (var j = 0; j < files.length; j++) total += files[j].size;
                    toastMsg('正在发送 ' + files.length + ' 个文件…');
                    xhr.upload.onprogress = function (e) {
                        if (!e.lengthComputable) return;
                        toastMsg('发送中 ' + Math.round(e.loaded / e.total * 100) + '%');
                    };
                    xhr.onload = function () {
                        if (xhr.status >= 200 && xhr.status < 300) {
                            toastMsg('已发送 ' + files.length + ' 个文件');
                            poll();
                        } else {
                            toastMsg('发送失败：' + (xhr.responseText || xhr.status));
                        }
                    };
                    xhr.onerror = function () { toastMsg('发送失败，请检查连接'); };
                    xhr.send(form);
                }

                msg.addEventListener('input', function () { sendBtn.disabled = !msg.value.trim(); });
                msg.addEventListener('keydown', function (e) {
                    if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); sendText(); }
                });
                sendBtn.onclick = sendText;
                fileBtn.onclick = function () { fileIn.click(); };
                fileIn.onchange = function () { upload(fileIn.files); fileIn.value = ''; };

                poll();
                timer = setInterval(poll, 1500);
                window.addEventListener('pagehide', function () { clearInterval(timer); });
            </script>
            </body>
            </html>
            """);
}
