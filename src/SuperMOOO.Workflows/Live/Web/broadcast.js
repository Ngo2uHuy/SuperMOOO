import { platforms, layouts, profileFromSearch } from './broadcast-config.mjs';

// Trang cho OBS Browser Source: video nền lặp; câu trả lời (giọng đọc) hoặc clip dựng sẵn do app đưa vào hàng, phát lần lượt.
const loop = document.getElementById('loop'), clip = document.getElementById('clip'), voice = document.getElementById('voice');
const message = document.getElementById('message'), stage = document.getElementById('stage');
const profile = profileFromSearch(location.search), layout = layouts[profile.layout];
let playing = 0, loopUrl = '';

document.title = `SuperMOOO · ${platforms[profile.platform].name} · ${layout.label}`;
stage.style.width = `min(100vw, ${100 * layout.width / layout.height}vh)`;
stage.style.height = `min(100vh, ${100 * layout.height / layout.width}vw)`;
loop.style.objectFit = clip.style.objectFit = profile.fit;

function needsGesture(play) {
  message.replaceChildren();
  const button = document.createElement('button');
  button.textContent = 'Phát video và âm thanh';
  button.onclick = async () => { try { await play(); message.hidden = true; } catch { button.textContent = 'Không phát được. Kiểm tra thiết bị âm thanh.'; } };
  message.append(button); message.hidden = false;
}

async function startLoop(url) {
  if (url === loopUrl) return;
  loopUrl = url;
  if (!url) { loop.hidden = true; loop.removeAttribute('src'); message.textContent = 'Chọn video nền ở tab Phát OBS trong app.'; message.hidden = false; return; }
  loop.src = url; loop.hidden = false;
  try { await loop.play(); message.hidden = true; } catch { needsGesture(() => loop.play()); }
}

async function done(seq) {
  playing = 0; clip.hidden = true; clip.removeAttribute('src');
  if (loopUrl) loop.play().catch(() => {});
  try { await fetch(`/api/done?seq=${seq}`, { method: 'POST' }); } catch { /* app đóng; hàng chờ tự xoá */ }
}

function play(item) {
  playing = item.seq;
  const finish = () => done(item.seq);
  if (item.kind === 'clip') {
    clip.src = item.url; clip.hidden = false; loop.pause();
    clip.onended = finish; clip.onerror = finish;
    clip.play().catch(() => needsGesture(() => clip.play()));
  } else {
    voice.src = item.url; voice.onended = finish; voice.onerror = finish;
    voice.play().catch(() => needsGesture(() => voice.play()));
  }
}

async function poll() {
  try {
    const now = await (await fetch('/api/now')).json();
    await startLoop(now.loop || '');
    if (loopUrl && !loop.paused) message.hidden = true;
    if (now.item && now.item.seq !== playing) play(now.item);
  } catch { message.textContent = 'Mất kết nối app SuperMOOO. Kiểm tra app còn chạy và máy chủ phát đang bật.'; message.hidden = false; }
  setTimeout(poll, 600);
}
poll();
