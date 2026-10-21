namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// The single HTML page the phone sees after scanning the QR code. Deliberately dependency-free
/// (one file input + XHR), so it renders identically in the system browser, WeChat's built-in
/// browser and iOS Safari. Upload progress rides on XHR's upload.onprogress; multiple files go
/// out in one form post, and the success toast invites another round.
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
            <title>手机传文件</title>
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
                body {
                    font-family: -apple-system, "PingFang SC", "Microsoft YaHei", sans-serif;
                    background: #f2f4f7; color: #1f2329; min-height: 100vh;
                    display: flex; flex-direction: column; align-items: center; padding: 24px 16px 48px;
                }
                .card {
                    width: 100%; max-width: 480px; background: #fff; border-radius: 16px;
                    padding: 22px 18px; box-shadow: 0 2px 12px rgba(15, 23, 42, .06); margin-bottom: 16px;
                }
                h1 { font-size: 19px; margin-bottom: 4px; }
                .hint { font-size: 13px; color: #8a919f; line-height: 1.6; }
                #drop {
                    border: 2px dashed #c9d1dc; border-radius: 12px; padding: 26px 12px;
                    text-align: center; color: #646a73; font-size: 14px; margin-top: 14px;
                }
                #drop.active { border-color: #4f7cff; color: #4f7cff; background: #f3f7ff; }
                button {
                    display: block; width: 100%; border: 0; border-radius: 10px; padding: 13px 0;
                    font-size: 16px; font-weight: 600; margin-top: 14px; cursor: pointer;
                }
                #pick { background: #eaf0ff; color: #2f5fe0; }
                #send { background: #4f7cff; color: #fff; }
                #send:disabled { background: #c3cdf5; }
                ul { list-style: none; margin-top: 12px; }
                li { font-size: 13px; padding: 9px 10px; border-radius: 8px; background: #f5f6f8;
                     margin-bottom: 8px; word-break: break-all; }
                li .bar { height: 4px; border-radius: 2px; background: #e2e6ec; margin-top: 7px; overflow: hidden; }
                li .bar i { display: block; height: 100%; width: 0; background: #4f7cff; transition: width .2s; }
                li.ok { color: #1a7f4b; }
                li.ok .bar { display: none; }
                li.fail { color: #c8412b; }
                #toast {
                    position: fixed; left: 50%; bottom: 40px; transform: translateX(-50%) translateY(20px);
                    background: rgba(31,35,41,.92); color: #fff; font-size: 14px; padding: 10px 18px;
                    border-radius: 8px; opacity: 0; pointer-events: none; transition: all .25s; max-width: 86vw;
                }
                #toast.show { opacity: 1; transform: translateX(-50%) translateY(0); }
                textarea {
                    width: 100%; border: 1px solid #c9d1dc; border-radius: 10px; padding: 10px 12px;
                    font-size: 15px; font-family: inherit; line-height: 1.5; resize: vertical;
                    margin-top: 14px; background: #fff; color: #1f2329;
                }
                textarea:focus { outline: none; border-color: #4f7cff; }
                #sendText { background: #4f7cff; color: #fff; }
                #sendText:disabled { background: #c3cdf5; }
                #hist li { cursor: pointer; }
                #hist .meta { font-size: 12px; color: #8a919f; margin-bottom: 3px; }
                #hist .kind { display: inline-block; font-size: 11px; color: #2f5fe0;
                              background: #eaf0ff; border-radius: 4px; padding: 0 5px; margin-left: 6px; }
                #hist .txt { font-size: 13px; color: #1f2329; }
            </style>
            </head>
            <body>
            <div class="card">
                <h1>手机传文件</h1>
                <p class="hint">选择照片、视频或任意文件，上传后立即出现在电脑上。文件仅在局域网内直接传输，不经过任何服务器。</p>
                <div id="drop">也可以把文件拖到这里</div>
                <button id="pick" type="button">选择文件</button>
                <button id="send" type="button" disabled>开始上传</button>
                <ul id="list"></ul>
            </div>
            <div class="card">
                <h1>发文本到电脑</h1>
                <p class="hint">输入文字点发送，电脑上会自动复制到剪贴板。</p>
                <textarea id="msg" rows="3" placeholder="输入要发送的文本…"></textarea>
                <button id="sendText" type="button" disabled>发送文本</button>
            </div>
            <div class="card" id="histCard" hidden>
                <h1>发送记录</h1>
                <p class="hint">保存在本机浏览器（最多 50 条），点一条可复制全文。</p>
                <ul id="hist"></ul>
            </div>
            <div id="toast"></div>
            <input id="file" type="file" multiple hidden>
            <script>
                var pickBtn = document.getElementById('pick'),
                    sendBtn = document.getElementById('send'),
                    input   = document.getElementById('file'),
                    list    = document.getElementById('list'),
                    drop    = document.getElementById('drop'),
                    toast   = document.getElementById('toast');

                // The upload URL must carry the session token — the page's own URL already has it.
                var uploadUrl = 'up?t=' + encodeURIComponent(new URLSearchParams(location.search).get('t') || '');

                function toastMsg(text) {
                    toast.textContent = text;
                    toast.classList.add('show');
                    setTimeout(function () { toast.classList.remove('show'); }, 2600);
                }
                function human(bytes) {
                    if (bytes > 1073741824) return (bytes / 1073741824).toFixed(2) + ' GB';
                    if (bytes > 1048576) return (bytes / 1048576).toFixed(1) + ' MB';
                    return Math.max(1, Math.round(bytes / 1024)) + ' KB';
                }
                pickBtn.onclick = function () { input.click(); };
                input.onchange = function () { render(input.files); };

                ['dragover', 'dragenter'].forEach(function (ev) {
                    drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.add('active'); });
                });
                ['dragleave', 'drop'].forEach(function (ev) {
                    drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.remove('active'); });
                });
                drop.addEventListener('drop', function (e) { render(e.dataTransfer.files); });

                function render(files) {
                    list.innerHTML = '';
                    for (var i = 0; i < files.length; i++) {
                        var li = document.createElement('li');
                        li.textContent = files[i].name + '（' + human(files[i].size) + '）';
                        var bar = document.createElement('div'); bar.className = 'bar';
                        bar.innerHTML = '<i></i>';
                        li.appendChild(bar);
                        list.appendChild(li);
                    }
                    sendBtn.disabled = files.length === 0;
                }

                sendBtn.onclick = function () {
                    if (input.files.length === 0) return;
                    sendBtn.disabled = true;
                    sendBtn.textContent = '正在上传…';
                    var form = new FormData();
                    for (var i = 0; i < input.files.length; i++) form.append('file', input.files[i]);

                    var xhr = new XMLHttpRequest();
                    xhr.open('POST', uploadUrl);
                    xhr.upload.onprogress = function (e) {
                        if (!e.lengthComputable) return;
                        var pct = Math.round(e.loaded / e.total * 100);
                        sendBtn.textContent = '正在上传… ' + pct + '%';
                        var bars = list.querySelectorAll('.bar i');
                        for (var i = 0; i < bars.length; i++) bars[i].style.width = pct + '%';
                    };
                    xhr.onload = function () {
                        sendBtn.textContent = '开始上传';
                        if (xhr.status === 200) {
                            Array.prototype.forEach.call(list.children, function (li) {
                                li.classList.add('ok'); li.classList.remove('fail');
                            });
                            toastMsg(xhr.responseText);
                            input.value = '';
                            sendBtn.disabled = true;
                        } else {
                            Array.prototype.forEach.call(list.children, function (li) { li.classList.add('fail'); });
                            toastMsg('上传失败：' + xhr.responseText);
                            sendBtn.disabled = false;
                        }
                    };
                    xhr.onerror = function () {
                        sendBtn.textContent = '开始上传';
                        sendBtn.disabled = false;
                        toastMsg('网络中断，请保持手机与电脑在同一 Wi-Fi 后重试');
                    };
                    xhr.send(form);
                };

                var msgBox = document.getElementById('msg'),
                    sendTextBtn = document.getElementById('sendText');
                var textUrl = 'text?t=' + encodeURIComponent(new URLSearchParams(location.search).get('t') || '');

                // --- 发送记录：localStorage only (nothing leaves this phone), newest first ---
                var HIST_KEY = 'csTransferHistory';
                function loadHist() {
                    try { return JSON.parse(localStorage.getItem(HIST_KEY) || '[]') || []; }
                    catch (e) { return []; }
                }
                function saveHist(items) {
                    try { localStorage.setItem(HIST_KEY, JSON.stringify(items.slice(0, 50))); } catch (e) {}
                }
                function renderHist() {
                    var items = loadHist();
                    document.getElementById('histCard').hidden = items.length === 0;
                    var box = document.getElementById('hist');
                    box.innerHTML = '';
                    for (var i = 0; i < items.length; i++) {
                        var li = document.createElement('li');
                        var meta = document.createElement('div'); meta.className = 'meta';
                        meta.textContent = items[i].tm;
                        var kind = document.createElement('span'); kind.className = 'kind';
                        kind.textContent = /^https?:\/\//i.test(items[i].t) ? '链接' : '文本';
                        meta.appendChild(kind);
                        var txt = document.createElement('div'); txt.className = 'txt';
                        var full = items[i].t;
                        txt.textContent = full.length > 60 ? full.slice(0, 60) + '…' : full;
                        li.appendChild(meta); li.appendChild(txt);
                        li.onclick = (function (text) {
                            return function () { copyText(text); };
                        })(full);
                        box.appendChild(li);
                    }
                }
                function copyText(text) {
                    // Clipboard API needs a secure context — LAN pages run on http, so fall
                    // back to the classic hidden-textarea + execCommand route.
                    var fallback = function () {
                        var ta = document.createElement('textarea');
                        ta.value = text;
                        ta.style.position = 'fixed'; ta.style.opacity = '0';
                        document.body.appendChild(ta); ta.select();
                        var ok = false;
                        try { ok = document.execCommand('copy'); } catch (e) {}
                        document.body.removeChild(ta);
                        toastMsg(ok ? '已复制' : '复制失败');
                    };
                    if (navigator.clipboard && window.isSecureContext) {
                        navigator.clipboard.writeText(text).then(function () { toastMsg('已复制'); }, fallback);
                    } else {
                        fallback();
                    }
                }
                renderHist();

                msgBox.oninput = function () { sendTextBtn.disabled = msgBox.value.length === 0; };
                sendTextBtn.onclick = function () {
                    var text = msgBox.value;
                    if (!text) return;
                    sendTextBtn.disabled = true;
                    sendTextBtn.textContent = '发送中…';
                    var xhr = new XMLHttpRequest();
                    xhr.open('POST', textUrl);
                    xhr.onload = function () {
                        sendTextBtn.textContent = '发送文本';
                        if (xhr.status === 200) {
                            // The reply carries the SERVER's clock — history times stay
                            // consistent even if the phone's clock drifts.
                            var time = '';
                            try { time = JSON.parse(xhr.responseText).time || ''; } catch (e) {}
                            if (!time) time = new Date().toTimeString().slice(0, 5);
                            var items = loadHist();
                            items.unshift({ t: text, tm: time });
                            saveHist(items);
                            renderHist();
                            msgBox.value = '';
                            sendTextBtn.disabled = true;
                            toastMsg('已发送到电脑');
                        } else {
                            sendTextBtn.disabled = false;
                            toastMsg('发送失败：' + xhr.responseText);
                        }
                    };
                    xhr.onerror = function () {
                        sendTextBtn.textContent = '发送文本';
                        sendTextBtn.disabled = false;
                        toastMsg('网络中断，请保持手机与电脑在同一 Wi-Fi 后重试');
                    };
                    xhr.send(text);
                };
            </script>
            </body>
            </html>
            """);
}
