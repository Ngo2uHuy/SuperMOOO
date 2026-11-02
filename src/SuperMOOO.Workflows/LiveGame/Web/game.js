'use strict';

(async () => {
  const el = id => document.getElementById(id);
  const stage = el('stage');

  const params = new URLSearchParams(location.search);
  const key = params.get('key') || '';
  const obs = params.get('obs') === '1';
  const localUrl = path => { const u = new URL(path, location.origin); u.searchParams.set('key', key); return u.href; };
  const themeUrl = file => localUrl('/theme/' + encodeURIComponent(file));
  let disposed = false, reconnectTimer, lastMessage = Date.now(), sessionId = '', userVolume = false;
  if (obs) document.body.classList.add('obs-mode');
  if (params.get('transparent') === '1') document.body.classList.add('transparent-mode');

  // Mặc định = các hằng số cũ; theme.json (nhúng, hoặc ghi đè ở DataRoot\themes\temple) chỉ cần ghi phần muốn đổi.
  const defaults = {
    title: 'THẮP HƯƠNG CẦU AN',
    hint: 'Bình luận "Nam mô A Di Đà Phật" để tham gia',
    notice: '',
    festivalTitle: 'ĐẠI LỄ PHÓNG QUANG',
    festivalSub: 'Tùy hỷ công đức · Chúc bạn bình an',
    backdrop: 'backdrop.jpg',
    music: 'temple_zen.mp3',
    musicVolume: 0.18,
    sfxVolume: 0.6,
    sprites: { atlas: 'monk.json', blue: 'monk_blue.png', gold: 'monk_gold.png' },
    sounds: { bell: 'bell.wav', mo: 'mo.wav', gong: 'gong.wav' },
    layout: {
      columns: 14, startY: 850, rowHeight: 42, depth: 900, spreadTop: 760, spreadBottom: 1020,
      scaleTop: 0.52, scaleGrow: 0.55, maxFull: 300,
      spawn: { x: 540, y: 1850 }, dots: { x: 40, y: 830, columns: 50, gapX: 20, gapY: 8 }
    },
    positions: { censer: { x: 540, y: 785 }, buddha: { x: 540, y: 340, radius: 300 }, lamps: [{ x: 150, y: 300 }, { x: 930, y: 300 }] },
    // Bục vinh danh top 3 của phiên: thứ tự hạng 1, 2, 3.
    podium: { scale: 0.62, slots: [{ x: 540, y: 660 }, { x: 330, y: 748 }, { x: 750, y: 748 }] },
    lamp: { thresholds: [50, 150, 300, 600, 1000, 2000, 4000, 8000, 15000, 30000] },
    rituals: [{ action: 'incense', icon: '🪔' }, { action: 'bow', icon: '🙏' }, { action: 'lotus', icon: '🪷' }, { action: 'festival', icon: '✨' }]
  };
  const isObject = v => v !== null && typeof v === 'object' && !Array.isArray(v);
  // Chỉ nhận khoá đã biết và đúng kiểu; theme.json ghi đè là file người dùng tự sửa nên không tin cấu trúc.
  function merge(base, extra) {
    if (!isObject(extra)) return base;
    const out = { ...base };
    for (const [k, v] of Object.entries(extra)) {
      if (!Object.prototype.hasOwnProperty.call(base, k) || k === '__proto__' || k === 'constructor') continue;
      if (isObject(base[k])) out[k] = merge(base[k], v);
      else if (Array.isArray(base[k]) ? Array.isArray(v) : typeof v === typeof base[k]) out[k] = v;
    }
    return out;
  }
  let theme = defaults;
  try {
    const response = await fetch(themeUrl('theme.json'), { signal: AbortSignal.timeout(8000) });
    if (response.ok) theme = merge(defaults, await response.json());
  } catch (error) { console.warn('Không đọc được theme.json, dùng mặc định:', error); }
  const L = theme.layout, P = theme.positions;
  const point = (p, fx, fy) => ({ x: Number.isFinite(p?.x) ? p.x : fx, y: Number.isFinite(p?.y) ? p.y : fy });
  const censer = point(P.censer, 540, 785), buddha = point(P.buddha, 540, 340);
  const ritualIcon = action => theme.rituals.find(r => r?.action === action)?.icon || '🪷';
  const simAction = { incense: 'gift_incense', bow: 'gift_bow', lotus: 'gift_lotus', festival: 'gift_big' };

  el('card-header-title').textContent = theme.title;
  el('card-header-sub').textContent = theme.hint;
  if (theme.backdrop !== 'backdrop.jpg') el('backdrop-img').src = themeUrl(theme.backdrop);

  function resizeStage() {
    const scale = Math.min(window.innerWidth / 1080, window.innerHeight / 1920);
    stage.style.transform = `scale(${scale}) translateZ(0)`;
    stage.style.left = `${(window.innerWidth - 1080 * scale) / 2}px`;
    stage.style.top = `${(window.innerHeight - 1920 * scale) / 2}px`;
  }
  resizeStage();
  window.addEventListener('resize', resizeStage);

  const app = new PIXI.Application();
  await app.init({ width: 1080, height: 1920, backgroundAlpha: 0, antialias: true, preference: 'webgl', resolution: 1 });
  el('canvas-container').appendChild(app.canvas);
  app.ticker.maxFPS = 30;

  // Texture vẽ sẵn một lần để sprite dùng chung (batch WebGL, không tessellate lại mỗi khung).
  function canvasTexture(size, draw) {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = size;
    draw(canvas.getContext('2d'), size);
    return PIXI.Texture.from(canvas);
  }
  const glowTexture = canvasTexture(128, (ctx, s) => {
    const g = ctx.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    g.addColorStop(0, 'rgba(255,255,255,1)');
    g.addColorStop(0.35, 'rgba(255,255,255,0.45)');
    g.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = g; ctx.fillRect(0, 0, s, s);
  });
  const smokeTexture = canvasTexture(36, (ctx, s) => {
    const g = ctx.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    g.addColorStop(0, '#d9ded8'); g.addColorStop(0.7, '#d9ded8'); g.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.globalAlpha = 0.32; ctx.fillStyle = g;
    ctx.beginPath(); ctx.arc(s / 2, s / 2, s / 2, 0, Math.PI * 2); ctx.fill();
  });
  function lotusTexture(size, outer, inner, core) {
    return canvasTexture(size, (ctx, s) => {
      ctx.translate(s / 2, s / 2);
      const r = s / 2;
      ctx.globalAlpha = 0.92;
      ctx.fillStyle = outer;
      for (let i = 0; i < 8; i++) {
        ctx.rotate(Math.PI / 4);
        ctx.beginPath(); ctx.ellipse(0, -r * 0.5, r * 0.18, r * 0.45, 0, 0, Math.PI * 2); ctx.fill();
      }
      ctx.fillStyle = inner;
      for (let i = 0; i < 5; i++) {
        ctx.rotate((Math.PI * 2) / 5);
        ctx.beginPath(); ctx.ellipse(0, -r * 0.3, r * 0.14, r * 0.3, 0, 0, Math.PI * 2); ctx.fill();
      }
      ctx.fillStyle = core;
      ctx.beginPath(); ctx.arc(0, 0, r * 0.16, 0, Math.PI * 2); ctx.fill();
    });
  }
  const lotusTextures = {
    small: lotusTexture(28, '#ff9fb3', '#ffc4d0', '#ffeaa7'),
    medium: lotusTexture(52, '#ff7f98', '#ffb3c2', '#ffeaa7'),
    large: lotusTexture(88, '#f5b82e', '#ffe08a', '#fff6d0')
  };

  // Avatar mặc định: vòng tròn màu theo tên + chữ cái đầu, không tải ảnh ngoài.
  const palette = ['#8a5a2b', '#2e6b5e', '#6b3f8a', '#2f5f9e', '#9e3b2f', '#7a6a1f', '#3f7a2f', '#5a4a8a'];
  const initialOf = name => (Array.from((name || '').trim())[0] || '?').toLocaleUpperCase('vi-VN');
  function colorOf(name) {
    let hash = 0;
    for (const ch of name || '') hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
    return palette[hash % palette.length];
  }
  const initialTextures = new Map();
  let fallbackInitial = null;
  function drawInitial(letter, color) {
    return canvasTexture(64, (ctx, s) => {
      ctx.fillStyle = color; ctx.beginPath(); ctx.arc(s / 2, s / 2, s / 2, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = '#fff8e6'; ctx.font = 'bold 34px "Segoe UI", Arial, sans-serif';
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillText(letter, s / 2, s / 2 + 2);
    });
  }
  function initialTexture(name) {
    const letter = initialOf(name), color = colorOf(name), id = letter + '|' + color;
    let texture = initialTextures.get(id);
    if (texture) return texture;
    // Chữ cái đầu của mọi ngôn ngữ là tập mở; giới hạn số texture giữ trong GPU.
    if (initialTextures.size >= 256) return fallbackInitial ??= drawInitial('?', palette[0]);
    texture = drawInitial(letter, color);
    initialTextures.set(id, texture);
    return texture;
  }
  function initialElement(name) {
    const div = document.createElement('div');
    div.className = 'avatar-initial';
    div.style.background = colorOf(name);
    div.textContent = initialOf(name);
    return div;
  }

  // Áo lam là sprite mặc định; theo dõi/chia sẻ đổi hẳn sang sprite áo vàng (cùng atlas).
  let blueSheet = null, goldSheet = null;
  try {
    const atlas = await (await fetch(themeUrl(theme.sprites.atlas), { signal: AbortSignal.timeout(8000) })).json();
    const loadSheet = async file => {
      const sheet = new PIXI.Spritesheet(await PIXI.Assets.load({ src: themeUrl(file), loadParser: 'loadTextures' }), structuredClone(atlas));
      await sheet.parse();
      return sheet;
    };
    blueSheet = await loadSheet(theme.sprites.blue);
    try { goldSheet = await loadSheet(theme.sprites.gold); }
    catch (error) { console.warn('Không nạp được sprite áo vàng, dùng tô màu:', error); }
  } catch (error) { console.error('Không nạp được sprite nhân vật:', error); }

  const backLayer = new PIXI.Container();
  const decorLayer = new PIXI.Container();
  const incenseLayer = new PIXI.Container();
  const peopleLayer = new PIXI.Container();
  peopleLayer.sortableChildren = true;
  const fxLayer = new PIXI.Container();
  app.stage.addChild(backLayer, decorLayer, incenseLayer, peopleLayer, fxLayer);

  // Hào quang tượng Phật: sáng nhẹ thường trực, rực lên khi đại lễ.
  const buddhaHalo = new PIXI.Sprite(glowTexture);
  buddhaHalo.anchor.set(0.5); buddhaHalo.tint = 0xffc94d; buddhaHalo.blendMode = 'add';
  buddhaHalo.position.set(buddha.x, buddha.y);
  const haloSize = (Number.isFinite(P.buddha?.radius) ? P.buddha.radius : 300) * 2;
  buddhaHalo.width = buddhaHalo.height = haloSize;
  backLayer.addChild(buddhaHalo);

  // Đèn lưu ly vẽ bằng Graphics; độ sáng theo tổng tim của phiên.
  const thresholds = theme.lamp.thresholds.filter(Number.isFinite).sort((a, b) => a - b);
  const lamps = (Array.isArray(P.lamps) ? P.lamps : []).slice(0, 8).map((pos, i) => {
    const at = point(pos, 150, 300);
    const root = new PIXI.Container();
    root.position.set(at.x, at.y);
    const glow = new PIXI.Sprite(glowTexture);
    glow.anchor.set(0.5); glow.tint = 0xffc861; glow.blendMode = 'add'; glow.position.set(0, 4);
    const frame = new PIXI.Graphics();
    frame.moveTo(0, -at.y).lineTo(0, -40).stroke({ color: 0xb8892f, width: 2 });
    frame.moveTo(-22, -40).lineTo(-28, -2).stroke({ color: 0xd4a843, width: 1.5 });
    frame.moveTo(22, -40).lineTo(28, -2).stroke({ color: 0xd4a843, width: 1.5 });
    frame.circle(0, -40, 5).fill(0xd4a843);
    frame.moveTo(-32, 0).quadraticCurveTo(0, 48, 32, 0).closePath().fill({ color: 0x5fb0a4, alpha: 0.6 }).stroke({ color: 0xe6c36a, width: 2 });
    frame.ellipse(0, 0, 32, 9).fill({ color: 0x9fe0d4, alpha: 0.5 }).stroke({ color: 0xe6c36a, width: 2 });
    frame.moveTo(0, 26).lineTo(0, 44).stroke({ color: 0xd4a843, width: 2 });
    frame.circle(0, 46, 3).fill(0xd4a843);
    const flame = new PIXI.Graphics().ellipse(0, -6, 5, 10).fill(0xffe39a).ellipse(0, -4, 2.5, 5).fill(0xffffff);
    root.addChild(glow, frame, flame);
    backLayer.addChild(root);
    return { glow, flame, phase: i * 1.7, level: -1, base: 0.1 };
  });
  function setLampLevel(likes) {
    const level = thresholds.filter(t => likes >= t).length;
    const ratio = thresholds.length ? level / thresholds.length : 0;
    for (const lamp of lamps) {
      if (lamp.level === level) continue;
      lamp.level = level;
      lamp.base = 0.12 + 0.88 * ratio;
      lamp.glow.width = lamp.glow.height = 90 + 190 * ratio;
      lamp.flame.alpha = level > 0 ? 0.55 + 0.45 * ratio : 0.25;
    }
  }
  setLampLevel(0);

  // Bệ bục vinh danh vẽ sẵn, chỉ hiện khi hạng đó có nhân vật đang đứng.
  const rankColors = [0xffd700, 0xd9dee6, 0xcd7f32];
  const podiumSlots = (Array.isArray(theme.podium.slots) ? theme.podium.slots : []).slice(0, 3);
  const podiumScale = Number.isFinite(theme.podium.scale) ? Math.max(0.2, Math.min(1.5, theme.podium.scale)) : 0.62;
  const pedestals = podiumSlots.map((slot, i) => {
    const at = point(slot, 540, 700);
    const root = new PIXI.Container();
    root.position.set(at.x, at.y);
    const glow = new PIXI.Sprite(glowTexture);
    glow.anchor.set(0.5); glow.tint = rankColors[i]; glow.blendMode = 'add'; glow.alpha = 0.35; glow.width = 200; glow.height = 80;
    const base = new PIXI.Graphics().ellipse(0, 0, 70, 18).fill({ color: 0x2a1a0c, alpha: 0.55 }).stroke({ color: rankColors[i], width: 3 });
    const badge = new PIXI.Text({ text: String(i + 1), style: { fontFamily: '"Segoe UI", Arial, sans-serif', fontSize: 18, fontWeight: '700', fill: '#fff5d9', stroke: { color: '#000000', width: 3 } } });
    badge.anchor.set(0.5); badge.position.set(-64, -6);
    root.addChild(glow, base, badge);
    root.visible = false;
    backLayer.addChild(root);
    return root;
  });

  // Trang trí mùng 1/rằm/ngày lễ: dây cờ ngũ sắc, đèn lồng đỏ, hoa; chỉ hiện khi snapshot báo ngày đặc biệt.
  const lanterns = [];
  (() => {
    const flags = new PIXI.Graphics();
    const colors = [0x2f6fd6, 0xf3c623, 0xd8342c, 0xf5f5f0, 0xf08a24];
    const curve = x => 238 + Math.sin((x / 1080) * Math.PI) * 52;
    flags.moveTo(0, curve(0));
    for (let x = 20; x <= 1080; x += 20) flags.lineTo(x, curve(x));
    flags.stroke({ color: 0xc9a14a, width: 2 });
    for (let i = 0, x = 24; x < 1060; x += 44, i++) {
      const y = curve(x), y2 = curve(x + 30);
      flags.poly([x, y, x + 30, y2, x + 15, (y + y2) / 2 + 36]).fill({ color: colors[i % colors.length], alpha: 0.95 });
    }
    decorLayer.addChild(flags);
    for (const [x, y] of [[55, 430], [1025, 430], [300, 320], [780, 320]]) {
      const root = new PIXI.Container();
      root.position.set(x, y - 70);
      const g = new PIXI.Graphics();
      g.moveTo(0, -(y - 70)).lineTo(0, 0).stroke({ color: 0x8a6a2a, width: 2 });
      g.roundRect(-12, 0, 24, 8, 3).fill(0xd4a843);
      g.ellipse(0, 40, 30, 34).fill(0xd7261e).stroke({ color: 0xf2c14e, width: 1.5 });
      g.moveTo(-14, 10).quadraticCurveTo(-24, 40, -14, 70).stroke({ color: 0xf2c14e, width: 1, alpha: 0.7 });
      g.moveTo(14, 10).quadraticCurveTo(24, 40, 14, 70).stroke({ color: 0xf2c14e, width: 1, alpha: 0.7 });
      g.roundRect(-12, 72, 24, 8, 3).fill(0xd4a843);
      g.moveTo(-6, 80).lineTo(-6, 108).moveTo(0, 80).lineTo(0, 112).moveTo(6, 80).lineTo(6, 108).stroke({ color: 0xf2c14e, width: 2 });
      const glow = new PIXI.Sprite(glowTexture);
      glow.anchor.set(0.5); glow.position.set(0, 40); glow.width = glow.height = 140; glow.tint = 0xff6a3d; glow.blendMode = 'add'; glow.alpha = 0.45;
      root.addChild(glow, g);
      decorLayer.addChild(root);
      lanterns.push(root);
    }
    const flowers = new PIXI.Graphics();
    for (const [cx, cy] of [[70, 770], [120, 790], [960, 790], [1010, 770]]) {
      for (let i = 0; i < 6; i++) {
        const a = (i / 6) * Math.PI * 2;
        flowers.circle(cx + Math.cos(a) * 11, cy + Math.sin(a) * 11, 8).fill(i % 2 ? 0xff8fb0 : 0xffb6c8);
      }
      flowers.circle(cx, cy, 7).fill(0xffd84a);
    }
    decorLayer.addChild(flowers);
    decorLayer.visible = false;
  })();

  const viewers = new Map();
  const viewerData = new Map();
  const pool = [];
  const particles = [];
  const particlePool = [];
  let socket = null;
  let audioContext = null;
  let audioEnabled = false;
  let currentAudio = null;
  let bgmAudio = null;
  let lastAudioSeq = 0;
  let lastEffectSeq = 0;
  let festivalUntil = 0;
  let comboUntil = 0;
  let comboCount = 0;
  let masterVolume = 0.8;
  let settings = { fps: 30, volume: 0.8, giftRules: [] };
  let simulated = false;
  let backgrounds = {};
  const BOW_MS = 1040;

  const label = (text, size, color = '#fff5d9') => new PIXI.Text({
    text,
    style: { fontFamily: '"Segoe UI", Arial, sans-serif', fontSize: size, fill: color, stroke: { color: '#000000', width: 3 }, align: 'center' }
  });

  function makePerson() {
    const root = new PIXI.Container();
    const halo = new PIXI.Sprite(glowTexture);
    halo.anchor.set(0.5); halo.tint = 0xffd36b; halo.blendMode = 'add'; halo.position.set(0, -34); halo.width = halo.height = 130; halo.visible = false;
    root.addChild(halo);
    let body;
    if (blueSheet?.animations.kneel) {
      body = new PIXI.Sprite(blueSheet.animations.kneel[0]);
      body.anchor.set(0.5, 1);
      body.scale.set(0.42);
    } else {
      body = new PIXI.Graphics().ellipse(0, -24, 16, 26).fill(0x4b6b9e);
    }
    root.addChild(body);
    // Avatar đã cắt tròn sẵn trong texture, không dùng mask để giữ batch WebGL.
    const avatar = new PIXI.Sprite(PIXI.Texture.EMPTY);
    avatar.anchor.set(0.5);
    avatar.position.set(0, -56);
    avatar.width = avatar.height = 34;
    const avatarBorder = new PIXI.Graphics().circle(0, -56, 18).stroke({ color: 0xffffff, width: 2 });
    root.addChild(avatarBorder, avatar);
    const nameLabel = label('', 12, '#ffeeb3');
    nameLabel.anchor.set(0.5);
    nameLabel.position.set(0, 18);
    root.addChild(nameLabel);
    const titleLabel = label('', 11, '#ffd36b');
    titleLabel.anchor.set(0.5);
    titleLabel.position.set(0, 33);
    root.addChild(titleLabel);
    const bubble = label('', 14, '#1b120c');
    bubble.style.stroke = null;
    bubble.style.fontWeight = '600';
    bubble.style.wordWrap = true;
    bubble.style.wordWrapWidth = 200;
    bubble.anchor.set(0.5);
    bubble.position.set(0, -96);
    const bubbleBg = new PIXI.Graphics();
    bubbleBg.position.set(0, -96);
    root.addChild(bubbleBg, bubble);
    bubbleBg.visible = bubble.visible = false;
    return {
      root, body, halo, avatar, avatarBorder, nameLabel, titleLabel, bubble, bubbleBg,
      name: '', title: '', rank: -1, borderKey: '', gold: false, texture: null, avatarUrl: '', action: 'kneel', actionStart: 0, until: 0, haloUntil: 0,
      bubbleUntil: 0, targetX: 0, targetY: 0, swayOffset: Math.random() * 10
    };
  }

  const avatarQueue = [];
  let avatarActive = 0;
  function clearAvatar(p) {
    p.version = (p.version || 0) + 1;
    p.avatar.texture = p.name ? initialTexture(p.name) : PIXI.Texture.EMPTY;
    p.avatar.width = p.avatar.height = 34;
    p.texture?.destroy(true); p.texture = null;
    p.avatarUrl = ''; p.pendingAvatar = ''; p.desiredAvatar = ''; p.retryAt = 0;
  }
  function releasePerson(p) {
    p.name = ''; clearAvatar(p); p.root.removeFromParent();
    p.displaySeat = null; p.gold = false; p.rank = -1; p.halo.visible = false;
    p.title = ''; p.titleLabel.text = ''; paintBorder(p);
    p.nameLabel.style.fill = '#ffeeb3'; p.body.tint = 0xffffff;
    p.bubble.text = ''; p.bubbleBg.clear(); p.bubbleUntil = 0; p.until = 0; p.haloUntil = 0; p.action = 'kneel';
    if (pool.length < 100) pool.push(p); else p.root.destroy({ children: true });
  }
  function loadAvatar(p, address) {
    if (p.desiredAvatar !== address) { clearAvatar(p); p.desiredAvatar = address; }
    if (!address) return;
    if (p.avatarUrl === address || p.pendingAvatar === address || Date.now() < (p.retryAt || 0) || avatarQueue.length >= 300) return;
    const version = p.version = (p.version || 0) + 1;
    p.pendingAvatar = address;
    avatarQueue.push({ p, address, version }); pumpAvatars();
  }
  function pumpAvatars() {
    while (!disposed && avatarActive < 4 && avatarQueue.length) {
      const { p, address, version } = avatarQueue.shift();
      if (p.version !== version) continue;
      avatarActive++;
      (async () => {
        let bitmap;
        try {
          const response = await fetch(localUrl('/avatar?u=' + encodeURIComponent(address)), { signal: AbortSignal.timeout(8000) });
          if (!response.ok) throw new Error('Avatar HTTP ' + response.status);
          bitmap = await createImageBitmap(await response.blob(), { resizeWidth: 64, resizeHeight: 64 });
          if (disposed || p.version !== version) return;
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 64;
          const ctx = canvas.getContext('2d'); ctx.beginPath(); ctx.arc(32, 32, 32, 0, Math.PI * 2); ctx.clip(); ctx.drawImage(bitmap, 0, 0);
          const texture = PIXI.Texture.from(canvas);
          p.avatar.texture = texture; p.texture?.destroy(true); p.texture = texture;
          p.avatar.width = p.avatar.height = 34; p.avatarUrl = address; p.retryAt = 0;
        } catch {
          if (p.version === version) p.retryAt = Date.now() + 30000;
        } finally {
          bitmap?.close();
          if (p.version === version) p.pendingAvatar = '';
          avatarActive--; pumpAvatars();
        }
      })();
    }
  }

  // Hạt dùng lại sprite từ pool; trần 350 để bão quà không làm tụt khung hình.
  function spawn(type, x, y, options = {}) {
    if (particles.length >= 350) return;
    const p = particlePool.pop() || { root: new PIXI.Sprite() };
    const s = p.root;
    s.anchor.set(0.5); s.position.set(x, y); s.rotation = 0; s.tint = 0xffffff; s.blendMode = 'normal';
    p.type = type; p.age = 0;
    if (type === 'smoke') {
      s.texture = smokeTexture; s.alpha = 0.32; s.scale.set(0.8); s.x += (Math.random() - 0.5) * 20;
      p.vx = (Math.random() - 0.5) * 35; p.vy = -35 - Math.random() * 45; p.vRot = (Math.random() - 0.5) * 0.4;
      p.life = 4 + Math.random() * 2.5;
      incenseLayer.addChild(s);
    } else if (type === 'rise') {
      s.texture = lotusTextures[options.size || 'small']; s.alpha = 0.92; s.scale.set(0.8 + Math.random() * 0.3);
      p.vx = (Math.random() - 0.5) * 40; p.vy = -55 - Math.random() * 65; p.vRot = (Math.random() - 0.5) * 0.8;
      p.life = 3.5 + Math.random() * 2;
      fxLayer.addChild(s);
    } else if (type === 'fall') {
      s.texture = lotusTextures[options.size || 'medium']; s.alpha = 0.95; s.scale.set(0.75 + Math.random() * 0.4);
      p.vx = 0; p.vy = 70 + Math.random() * 70; p.vRot = (Math.random() - 0.5) * 0.9;
      p.sway = 20 + Math.random() * 30; p.phase = Math.random() * 6; p.baseX = x;
      p.life = 11 + Math.random() * 3;
      fxLayer.addChild(s);
    } else {
      s.texture = glowTexture; s.tint = options.tint || 0xffd76a; s.blendMode = 'add'; s.alpha = 1; s.scale.set(0.12 + Math.random() * 0.1);
      const angle = Math.random() * Math.PI * 2, speed = 120 + Math.random() * 160;
      p.vx = Math.cos(angle) * speed; p.vy = Math.sin(angle) * speed - 60; p.vRot = 0;
      p.life = 0.7 + Math.random() * 0.5;
      fxLayer.addChild(s);
    }
    particles.push(p);
  }
  const lotusRain = (size, count) => { for (let i = 0; i < count; i++) spawn('fall', 40 + Math.random() * 1000, 240 + Math.random() * 120, { size }); };
  const burst = (p, type, count, options) => {
    const x = p ? p.root.x : 540, y = p ? p.root.y - 30 : 1500;
    for (let i = 0; i < count; i++) spawn(type, x + (Math.random() - 0.5) * 50, y, options);
  };

  function paintBorder(p) {
    const key = p.rank + '|' + p.gold;
    if (p.borderKey === key) return;
    p.borderKey = key;
    const style = p.rank >= 0 ? { color: rankColors[p.rank], width: 4 } : p.gold ? { color: 0xffd700, width: 3.5 } : { color: 0xffffff, width: 2 };
    p.avatarBorder.clear().circle(0, -56, 18).stroke(style);
  }
  function applyLook(p, v) {
    if (p.gold !== v.gold) {
      p.gold = v.gold;
      p.nameLabel.style.fill = v.gold ? '#ffd700' : '#ffeeb3';
      p.body.tint = v.gold && !goldSheet ? 0xffd25f : 0xffffff;
    }
    paintBorder(p);
  }
  function placePerson(v) {
    let p = viewers.get(v.id);
    if (!p) {
      p = pool.pop() || makePerson();
      p.root.position.set(point(L.spawn, 540, 1850).x, point(L.spawn, 540, 1850).y);
      peopleLayer.addChild(p.root);
      viewers.set(v.id, p);
    }
    if (p.name !== v.name) { p.name = v.name; p.nameLabel.text = v.name; if (!p.texture) p.avatar.texture = initialTexture(v.name); }
    const title = v.title || '';
    if (p.title !== title) { p.title = title; p.titleLabel.text = title ? `✦ ${title} ✦` : ''; }
    return p;
  }

  let topIds = [];
  function layoutViewers() {
    // Top 3 của phiên đứng bục riêng, không chiếm chỗ quỳ và không bị thu thành chấm.
    const podiumRank = new Map(topIds.map((id, rank) => [id, rank]).filter(([id, rank]) => viewerData.has(id) && rank < podiumSlots.length));
    const list = [...viewerData.values()].filter(v => !podiumRank.has(v.id)).sort((a, b) => b.seen - a.seen);
    const maxFull = Math.max(0, L.maxFull | 0), cols = Math.max(1, L.columns | 0);
    const activeIds = new Set(viewerData.keys());
    const fullIds = new Set(list.slice(0, maxFull).map(v => v.id));
    const seats = new Set();
    for (const [id, p] of viewers) {
      if (!activeIds.has(id)) { releasePerson(p); viewers.delete(id); }
      else if (!fullIds.has(id)) p.displaySeat = null;
      else if (p.displaySeat != null) seats.add(p.displaySeat);
    }
    for (const [id, rank] of podiumRank) {
      const v = viewerData.get(id), p = placePerson(v), slot = point(podiumSlots[rank], 540, 700);
      p.full = true; p.rank = rank;
      p.body.visible = p.nameLabel.visible = p.titleLabel.visible = p.avatar.visible = p.avatarBorder.visible = true;
      p.targetX = slot.x; p.targetY = slot.y;
      p.root.scale.set(podiumScale); p.root.zIndex = Math.floor(slot.y);
      applyLook(p, v);
      loadAvatar(p, v.avatar);
    }
    const used = new Set(podiumRank.values());
    pedestals.forEach((pedestal, i) => { pedestal.visible = used.has(i); });
    list.forEach((v, index) => {
      const p = placePerson(v);
      p.rank = -1;
      p.full = index < maxFull;
      p.body.visible = p.nameLabel.visible = p.titleLabel.visible = p.avatar.visible = p.avatarBorder.visible = p.full;
      if (!p.full) {
        // Người ít tương tác thu nhỏ thành chấm để giữ khung hình khi phòng đông.
        if (p.texture || p.pendingAvatar) clearAvatar(p);
        const d = L.dots, k = index - maxFull;
        p.targetX = d.x + (k % d.columns) * d.gapX; p.targetY = d.y + Math.floor(k / d.columns) * d.gapY;
        p.root.scale.set(0.25); p.root.zIndex = Math.floor(p.targetY); return;
      }
      if (p.displaySeat == null) { let next = 0; while (seats.has(next)) next++; p.displaySeat = next; seats.add(next); }
      const col = p.displaySeat % cols, row = Math.floor(p.displaySeat / cols);
      const targetY = L.startY + row * L.rowHeight;
      const progress = (targetY - L.startY) / Math.max(1, L.depth);
      const spread = L.spreadTop + progress * (L.spreadBottom - L.spreadTop);
      p.targetX = (1080 - spread) / 2 + (cols > 1 ? col / (cols - 1) : 0.5) * spread;
      p.targetY = targetY;
      p.root.scale.set(L.scaleTop + progress * L.scaleGrow);
      p.root.zIndex = Math.floor(targetY);
      applyLook(p, v);
      loadAvatar(p, v.avatar);
    });
    el('hud-viewers').textContent = viewers.size;
  }

  function showBubble(p, text) {
    p.bubble.text = text;
    p.bubbleUntil = performance.now() + 5000;
    const padX = 14, padY = 8, bw = p.bubble.width + padX * 2, bh = p.bubble.height + padY * 2;
    p.bubbleBg.clear()
      .roundRect(-bw / 2, -bh / 2, bw, bh, 10).fill({ color: 0xfffcf0, alpha: 0.95 })
      .poly([-6, bh / 2, 6, bh / 2, 0, bh / 2 + 8]).fill(0xfffcf0);
  }
  function animate(p, action, ms) {
    p.action = action; p.actionStart = performance.now(); p.until = p.actionStart + ms;
  }

  function showGiftToast(name, description, icon, avatarUrl) {
    const container = el('gift-alerts-container');
    while (container.children.length >= 3) container.firstChild.remove();
    const toast = document.createElement('div');
    toast.className = 'gift-toast';
    let picture = initialElement(name);
    if (avatarUrl) {
      const image = document.createElement('img');
      image.alt = '';
      image.onerror = () => image.replaceWith(initialElement(name));
      image.src = localUrl('/avatar?u=' + encodeURIComponent(avatarUrl));
      picture = image;
    }
    const text = document.createElement('div'); text.className = 'gift-toast-text';
    const title = document.createElement('span'); title.className = 'gift-toast-name'; title.textContent = name;
    const desc = document.createElement('span'); desc.className = 'gift-toast-desc'; desc.textContent = 'tùy hỷ ' + description;
    const badge = document.createElement('div'); badge.className = 'gift-toast-icon'; badge.textContent = icon;
    text.append(title, desc); toast.append(picture, text, badge);
    container.appendChild(toast);
    setTimeout(() => {
      toast.style.transition = 'all 0.4s ease';
      toast.style.opacity = '0';
      toast.style.transform = 'translateX(-60px)';
      setTimeout(() => toast.remove(), 400);
    }, 4000);
  }

  function triggerCombo() {
    const now = performance.now();
    comboCount = now < comboUntil ? comboCount + 1 : 1;
    comboUntil = now + 3500;
    if (comboCount < 2) return;
    el('combo-text').textContent = `Tùy hỷ liên tiếp x${comboCount}`;
    el('combo-badge').classList.add('active');
  }

  function startFestival(name) {
    el('festival-title').textContent = theme.festivalTitle;
    el('festival-name').textContent = name;
    el('festival-sub').textContent = theme.festivalSub;
    el('festival-overlay').classList.add('active');
    festivalUntil = performance.now() + 10000;
    lotusRain('large', 30);
  }

  function handleEffect(e) {
    const p = viewers.get(e.userId);
    const data = viewerData.get(e.userId);
    const name = (e.isGift && e.text) || data?.name || 'Bạn';
    const avatar = data?.avatar || '';
    const gift = () => `${e.giftLabel} (${e.coins} xu${e.count > 1 ? ' × ' + e.count : ''})`;
    const count = Math.max(1, e.count || 1);
    // Hành động riêng của theme mượn hiệu ứng dựng sẵn, chỉ đổi biểu tượng và âm thanh.
    const icon = e.icon || ritualIcon(e.action);
    const play = name => sound(e.sound || name);
    const giftToast = () => { if (e.isGift) { showGiftToast(name, gift(), icon, avatar); triggerCombo(); } };
    switch (e.action) {
      case 'chat':
        if (p?.full) showBubble(p, e.text);
        sound('mo');
        break;
      case 'chant':
        if (p) { animate(p, 'bow', BOW_MS); p.haloUntil = performance.now() + 2600; }
        play('mo');
        giftToast();
        break;
      case 'bow':
        // Lễ Phật bằng quà: lạy ba lạy và chuông; niệm thường chỉ một lạy.
        if (p) animate(p, 'bow', e.isGift ? BOW_MS * 3 : BOW_MS);
        if (e.isGift || e.sound) play('bell');
        giftToast();
        break;
      case 'heart':
        burst(p, 'rise', Math.min(12, count * 2), { size: 'small' });
        if (e.sound) play();
        giftToast();
        break;
      case 'follow':
      case 'spark':
        burst(p, 'spark', 18);
        if (e.sound) play();
        giftToast();
        break;
      case 'title':
        if (p) { p.haloUntil = performance.now() + 3000; if (p.full) showBubble(p, `✦ ${e.text} ✦`); }
        burst(p, 'spark', 14, { tint: 0xffe08a });
        sound('bell', 0.5);
        break;
      case 'incense':
        if (p) animate(p, 'incense', 2600);
        for (let i = 0; i < 15; i++) spawn('smoke', censer.x, censer.y);
        play('mo');
        giftToast();
        break;
      case 'lotus':
        lotusRain('medium', Math.min(40, 10 + count * 5));
        burst(p, 'rise', 6, { size: 'medium' });
        play('bell');
        giftToast();
        break;
      case 'festival':
        startFestival(name);
        play('gong');
        giftToast();
        break;
    }
  }

  // Âm thanh nghi lễ: phát file của gói chủ đề; thiếu file thì tổng hợp bằng Web Audio như bản cũ.
  const buffers = {};
  let buffersLoading = null;
  const lastSound = {};
  async function loadSounds() {
    for (const [name, file] of Object.entries(theme.sounds)) {
      if (typeof file !== 'string') continue;
      try {
        const response = await fetch(themeUrl(file), { signal: AbortSignal.timeout(8000) });
        if (response.ok) buffers[name] = await audioContext.decodeAudioData(await response.arrayBuffer());
      } catch (error) { console.warn('Không nạp được âm thanh', name, error); }
    }
  }
  function synth(name, volume) {
    const now = audioContext.currentTime;
    const tone = (type, from, to, gain, duration) => {
      const osc = audioContext.createOscillator(), env = audioContext.createGain();
      osc.type = type; osc.frequency.setValueAtTime(from, now);
      if (to !== from) osc.frequency.exponentialRampToValueAtTime(to, now + duration * 0.8);
      env.gain.setValueAtTime(volume * gain, now); env.gain.exponentialRampToValueAtTime(0.0001, now + duration);
      osc.connect(env).connect(audioContext.destination); osc.start(now); osc.stop(now + duration);
    };
    if (name === 'mo') tone('triangle', 480, 160, 0.6, 0.1);
    else if (name === 'gong') [[98, 1, 5], [196, 0.45, 4], [292, 0.25, 3]].forEach(([f, g, d]) => tone('sine', f, f, g, d));
    else [[261.63, 1, 4.5], [523.25, 0.5, 3.5], [784.88, 0.25, 2.8], [1046.5, 0.15, 2]].forEach(([f, g, d]) => tone('sine', f, f, g * 0.6, d));
  }
  function sound(name, gain = 1) {
    if (!audioEnabled || !audioContext) return;
    const gap = name === 'mo' ? 250 : name === 'gong' ? 3000 : 500;
    if (performance.now() - (lastSound[name] || 0) < gap) return;
    lastSound[name] = performance.now();
    const volume = masterVolume * theme.sfxVolume * gain;
    if (volume <= 0) return;
    const buffer = buffers[name];
    if (!buffer) { synth(name, volume); return; }
    const source = audioContext.createBufferSource(), env = audioContext.createGain();
    source.buffer = buffer; env.gain.value = volume;
    source.connect(env).connect(audioContext.destination); source.start();
  }

  const musicVolume = () => theme.musicVolume * masterVolume;
  // Nhạc người dùng (DataRoot\themes\temple\music) phát ngẫu nhiên lần lượt, không lặp liền bài; bài lỗi bị bỏ qua đến hết phiên.
  // Không có bài nào dùng được thì lặp nhạc nền mặc định của theme.
  let playlist = [], badTracks = new Set(), track = -1;
  function nextTrack() {
    if (!bgmAudio) return;
    const ok = playlist.map((_, i) => i).filter(i => !badTracks.has(i));
    const choices = ok.length > 1 ? ok.filter(i => i !== track) : ok;
    track = choices.length ? choices[Math.floor(Math.random() * choices.length)] : -1;
    if (track >= 0) { bgmAudio.loop = false; bgmAudio.src = localUrl(playlist[track]); }
    else if (theme.music) { bgmAudio.loop = true; bgmAudio.src = themeUrl(theme.music); }
    else { bgmAudio.removeAttribute('src'); return; }
    if (audioEnabled) bgmAudio.play().catch(() => {});
  }
  function setPlaylist(list) {
    const next = Array.isArray(list) ? list.filter(u => typeof u === 'string').slice(0, 300) : [];
    if (next.join('\n') === playlist.join('\n')) return;
    playlist = next; badTracks = new Set();
    // Đang phát nhạc mặc định mà có playlist mới thì chuyển sang playlist.
    if (bgmAudio && track < 0 && playlist.length) nextTrack();
  }
  function playMusic() {
    if (!audioEnabled || (!theme.music && !playlist.length)) return;
    if (!bgmAudio) {
      bgmAudio = new Audio();
      bgmAudio.onended = () => { if (track >= 0) nextTrack(); };
      bgmAudio.onerror = () => { if (track >= 0) { badTracks.add(track); nextTrack(); } };
      nextTrack();
    }
    bgmAudio.volume = Math.min(1, currentAudio ? 0.05 * masterVolume : musicVolume());
    bgmAudio.play().catch(() => {});
  }
  function stopVoice() {
    if (!currentAudio) return;
    try { currentAudio.pause(); currentAudio.removeAttribute('src'); currentAudio.load(); } catch { }
    currentAudio = null;
    el('subtitle-bar').classList.remove('active');
  }
  function updateSoundButton() {
    const button = el('sound-btn');
    button.textContent = audioEnabled ? '🔊 Đang bật âm thanh (bấm để tắt)' : '🔇 Bật âm thanh (nhạc và giọng đọc)';
    button.style.background = audioEnabled ? 'rgba(255, 215, 0, 0.45)' : 'rgba(255, 255, 255, 0.15)';
  }
  async function setAudio(on) {
    if (on) {
      audioContext ??= new AudioContext();
      // Chính sách tự phát có thể giữ resume() chờ thao tác người dùng; không treo trang vì việc đó.
      await Promise.race([audioContext.resume().catch(() => {}), new Promise(resolve => setTimeout(resolve, 1000))]);
      if (audioContext.state !== 'running') { updateSoundButton(); return false; }
      audioEnabled = true;
      buffersLoading ??= loadSounds();
      playMusic();
      sound('bell', 0.6);
    } else {
      audioEnabled = false;
      bgmAudio?.pause();
      stopVoice();
      await audioContext?.suspend();
    }
    updateSoundButton();
    return true;
  }
  window.toggleAudio = () => setAudio(!audioEnabled);
  function applyVolume(value) {
    masterVolume = Math.max(0, Math.min(1, value));
    el('volume-slider').value = Math.round(masterVolume * 100);
    el('volume-val-txt').textContent = `${Math.round(masterVolume * 100)}%`;
    if (bgmAudio) bgmAudio.volume = Math.min(1, currentAudio ? 0.05 * masterVolume : musicVolume());
    if (currentAudio) currentAudio.volume = masterVolume;
  }

  function playVoice(item) {
    if (!item || item.seq <= lastAudioSeq) return;
    lastAudioSeq = item.seq;
    if (!audioEnabled || Date.parse(item.expires) <= Date.now()) return;
    stopVoice();
    if (bgmAudio) bgmAudio.volume = 0.05 * masterVolume;
    const audio = new Audio(localUrl(item.url));
    currentAudio = audio;
    audio.volume = masterVolume;
    const sub = el('subtitle-bar');
    sub.textContent = item.text;
    sub.classList.add('active');
    const finish = () => {
      if (currentAudio !== audio) return;
      stopVoice();
      if (bgmAudio) bgmAudio.volume = musicVolume();
    };
    audio.onended = finish;
    audio.onerror = finish;
    audio.play().catch(finish);
  }

  const compact = n => n >= 1e6 ? (n / 1e6).toFixed(1).replace('.', ',') + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1).replace('.', ',') + 'K' : String(n);
  const number = n => Number(n || 0).toLocaleString('vi-VN');

  function renderRituals() {
    const rules = [...(settings.giftRules || [])].sort((a, b) => a.minCoins - b.minCoins);
    const box = el('ritual-items-box');
    box.replaceChildren(...rules.map(rule => {
      const item = document.createElement('div');
      item.className = 'ritual-item';
      if (simAction[rule.action]) item.dataset.sim = simAction[rule.action];
      const icon = document.createElement('span'); icon.className = 'ritual-icon'; icon.textContent = ritualIcon(rule.action);
      const cost = document.createElement('span'); cost.className = 'ritual-cost'; cost.textContent = `${number(rule.minCoins)} xu`;
      const text = document.createElement('span'); text.className = 'ritual-label'; text.textContent = rule.label;
      item.append(icon, cost, text);
      return item;
    }));
    const button = (sim, text, full) => {
      const b = document.createElement('button');
      b.className = 'btn-sim' + (full ? ' btn-sim-full' : '');
      b.dataset.sim = sim; b.textContent = text;
      return b;
    };
    el('sim-grid-container').replaceChildren(
      button('member', '👤 Vào phòng'), button('chat', '🙏 Niệm Phật'), button('like', '❤️ Thả tim'), button('follow', '⭐ Theo dõi'),
      ...rules.filter(r => simAction[r.action]).map(r => button(simAction[r.action], `${ritualIcon(r.action)} ${number(r.minCoins)} xu · ${r.label}`)),
      button('storm', '🌪️ Bão quà (100 sự kiện)', true));
  }

  let headKey = '';
  function applyHead(h) {
    if (!h) return;
    el('likes-count').textContent = compact(h.likes);
    el('viewer-count').textContent = compact(h.viewerCount);
    el('calendar-text').textContent = h.calendar;
    el('incense-stats').textContent = `🪔 ${number(h.incense)} nén hương · 🙏 ${number(h.prayers)} lời niệm`;
    el('donor-leaderboard-text').textContent = h.top?.length
      ? h.top.map((d, i) => `Hạng ${i + 1}: ${d.nickname} (${number(d.coins)} xu)`).join(' · ')
      : 'Chưa có';
    el('connection-status').textContent = `📡 ${h.status}`;
    el('notice-text').textContent = h.notice ? '⚠️ ' + h.notice : '';
    setLampLevel(h.likes);
    const top = (h.top || []).slice(0, 3).map(d => String(d.userId || ''));
    if (top.join('|') !== topIds.join('|')) { topIds = top; layoutViewers(); }
    decorLayer.visible = !!h.holiday;
    const marquee = [`🪷 ${h.calendar}`, theme.hint, theme.notice].filter(Boolean).join('   ·   ');
    if (el('marquee-text').textContent !== marquee) el('marquee-text').textContent = marquee;
    const signature = JSON.stringify([h.lighting, backgrounds]);
    if (signature === headKey) return;
    headKey = signature;
    const video = el('backdrop-video'), source = backgrounds[h.lighting] ? localUrl(backgrounds[h.lighting]) : '';
    if ((video.dataset.source || '') !== source) {
      video.dataset.source = source;
      video.onplaying = () => { if (video.dataset.source) video.style.display = 'block'; };
      video.onerror = () => { video.style.display = 'none'; };
      if (source) { video.src = source; video.play().catch(() => { video.style.display = 'none'; }); }
      else { video.pause(); video.removeAttribute('src'); video.load(); video.style.display = 'none'; }
    }
    el('backdrop-img').style.filter = h.lighting === 'night' ? 'brightness(.65)' : h.lighting === 'afternoon' ? 'sepia(.15)' : 'none';
  }

  function applyEffects(list) {
    for (const e of (list || []).filter(e => e.seq > lastEffectSeq).slice(-32)) {
      // Hiệu ứng cũ (khi tải lại trang) chỉ cập nhật mốc, không diễn lại.
      if (Date.now() - Date.parse(e.at) < 5000) handleEffect(e);
      lastEffectSeq = e.seq;
    }
  }

  function resetSession() {
    lastAudioSeq = lastEffectSeq = 0;
    for (const p of viewers.values()) releasePerson(p);
    viewers.clear(); viewerData.clear(); avatarQueue.length = 0;
    for (const p of particles) { p.root.removeFromParent(); particlePool.push(p); }
    particles.length = 0;
    stopVoice();
    el('gift-alerts-container').replaceChildren();
    festivalUntil = 0; userVolume = false; headKey = ''; topIds = [];
    pedestals.forEach(pedestal => { pedestal.visible = false; });
  }

  function onMessage(m) {
    if (m.t === 'full') {
      if (sessionId !== m.session) { resetSession(); sessionId = m.session; }
      simulated = !!m.simulated;
      settings = m.settings || settings;
      backgrounds = m.backgrounds || {};
      setPlaylist(m.playlist);
      app.ticker.maxFPS = settings.fps === 60 ? 60 : 30;
      if (!userVolume) applyVolume(settings.volume);
      renderRituals();
      viewerData.clear();
      for (const v of m.viewers || []) viewerData.set(v.id, v);
      headKey = '';
      applyHead(m.head);
      layoutViewers();
      applyEffects(m.effects);
      if (m.audio) playVoice(m.audio);
    } else if (m.t === 'delta') {
      if (m.head) applyHead(m.head);
      if (m.viewers) {
        for (const id of m.viewers.remove || []) viewerData.delete(id);
        for (const v of m.viewers.upsert || []) viewerData.set(v.id, v);
        for (const [id, seen] of m.viewers.touch || []) { const v = viewerData.get(id); if (v) v.seen = seen; }
        layoutViewers();
      }
      if (m.effects) applyEffects(m.effects);
      if (m.audio) playVoice(m.audio);
    }
  }

  let lastPingTime = 0;
  function connectWebSocket() {
    if (disposed) return;
    clearTimeout(reconnectTimer);
    socket = new WebSocket(`ws://${location.host}/ws?key=${encodeURIComponent(key)}`);
    socket.onopen = () => {
      lastMessage = Date.now();
      lastPingTime = performance.now();
      el('connection-status').textContent = '✅ Đã kết nối';
    };
    socket.onmessage = e => {
      lastMessage = Date.now();
      const ping = Math.round(performance.now() - lastPingTime);
      lastPingTime = performance.now();
      if (ping < 600) el('hud-ping').textContent = `${ping}ms`;
      try { onMessage(JSON.parse(e.data)); }
      catch (error) { console.error('Lỗi xử lý trạng thái:', error); }
    };
    socket.onclose = () => {
      el('connection-status').textContent = '⚠️ Mất kết nối · thử lại sau 2 giây...';
      el('hud-ping').textContent = 'MẤT KẾT NỐI';
      if (!disposed) reconnectTimer = setTimeout(connectWebSocket, 2000);
    };
    socket.onerror = () => socket.close();
  }

  // Chỉ endpoint giả lập được ghi sự kiện; WebSocket vẫn chỉ nhận trạng thái.
  let simulationPending = false;
  async function simulate(action, text) {
    if (obs) return false;
    if (!simulated) { el('connection-status').textContent = 'Chỉ giả lập được trong phiên giả lập ở S24.'; return false; }
    if (simulationPending) return false;
    simulationPending = true;
    try {
      const response = await fetch(localUrl('/api/live/simulate'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, text }), signal: AbortSignal.timeout(5000)
      });
      if (!response.ok) throw new Error(response.status === 429 ? 'Hàng đợi bận, thử lại sau.' : 'Không gửi được sự kiện (' + response.status + ').');
      return true;
    } catch (error) { el('connection-status').textContent = error.message; return false; }
    finally { simulationPending = false; }
  }
  async function sendChat() {
    const input = el('chat-input'), text = input.value.trim();
    if (text && await simulate('custom_chat', text.slice(0, 160)) && input.value.trim() === text) input.value = '';
  }
  function toggleSimDock() {
    const dock = el('simulator-dock');
    dock.classList.toggle('hidden');
    el('toggle-sim-btn').classList.toggle('visible', dock.classList.contains('hidden'));
  }
  function copyObsUrl() {
    const target = new URL(location.href);
    target.pathname = '/'; target.searchParams.set('obs', '1');
    const url = target.href;
    navigator.clipboard.writeText(url).then(() => {
      const button = el('btn-copy-obs'), text = button.textContent;
      button.textContent = '✅ Đã sao chép URL phát';
      setTimeout(() => { button.textContent = text; }, 2500);
    }).catch(() => prompt('Sao chép URL phát:', url));
  }
  const commands = { sendChat, toggleSimDock, toggleAudio: window.toggleAudio, copyObsUrl };
  document.addEventListener('click', event => {
    const target = event.target.closest('[data-sim],[data-action]');
    if (!target || obs) return;
    if (target.dataset.sim) simulate(target.dataset.sim);
    else commands[target.dataset.action]?.();
  });
  el('chat-input').addEventListener('keydown', event => { if (event.key === 'Enter') sendChat(); });
  el('volume-slider').addEventListener('input', event => { userVolume = true; applyVolume(Number(event.target.value) / 100); });
  window.addEventListener('keydown', e => {
    if (obs || e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;
    const k = e.key.toLowerCase();
    const rules = [...(settings.giftRules || [])].sort((a, b) => a.minCoins - b.minCoins).filter(r => simAction[r.action]);
    if (k === ' ') { e.preventDefault(); simulate('like'); }
    else if (k >= '1' && k <= '4' && rules[Number(k) - 1]) simulate(simAction[rules[Number(k) - 1].action]);
    else if (k === 'm') window.toggleAudio();
    else if (k === 'h') toggleSimDock();
  });

  let smokeTimer = 0, fpsTimer = 0, frameCount = 0, rainTimer = 0, lastTick = performance.now();
  app.ticker.add(ticker => {
    const dt = Math.min(ticker.deltaMS / 1000, 0.1);
    const now = performance.now();
    lastTick = now;

    frameCount++;
    fpsTimer += dt;
    if (fpsTimer >= 0.5) { el('hud-fps').textContent = Math.round(frameCount / fpsTimer); frameCount = 0; fpsTimer = 0; }

    smokeTimer += dt;
    if (smokeTimer > 0.16) { smokeTimer = 0; spawn('smoke', censer.x, censer.y); }

    const festival = now < festivalUntil;
    if (festival) {
      rainTimer += dt;
      if (rainTimer > 0.08) { rainTimer = 0; spawn('fall', 40 + Math.random() * 1000, 240, { size: 'large' }); }
    }
    buddhaHalo.alpha = festival ? 0.85 + Math.sin(now / 160) * 0.1 : 0.32 + Math.sin(now / 1200) * 0.08;
    buddhaHalo.width = buddhaHalo.height = haloSize * (festival ? 1.35 + Math.sin(now / 300) * 0.05 : 1);
    for (const lamp of lamps) lamp.glow.alpha = lamp.base * (0.9 + 0.1 * Math.sin(now / 180 + lamp.phase));
    if (decorLayer.visible) lanterns.forEach((l, i) => { l.rotation = Math.sin(now / 900 + i) * 0.05; });

    for (const p of viewers.values()) {
      const breathing = Math.sin(now * 0.002 + p.swayOffset) * 0.18;
      p.root.x += (p.targetX - p.root.x) * Math.min(1, dt * 2.5);
      p.root.y += (p.targetY + breathing - p.root.y) * Math.min(1, dt * 2.5);
      const moving = Math.abs(p.targetY - p.root.y) > 6;
      let anim = 'kneel';
      if (moving) anim = 'walk';
      else if (now < p.until && (p.action === 'bow' || p.action === 'incense')) anim = p.action;
      const sheet = p.gold && goldSheet ? goldSheet : blueSheet;
      if (p.full && sheet?.animations[anim]) {
        const frames = sheet.animations[anim];
        const frame = anim === 'kneel' ? 0 : Math.floor((now - (anim === 'walk' ? 0 : p.actionStart)) / 130) % frames.length;
        p.body.texture = frames[frame];
      }
      const halo = p.full && now < p.haloUntil;
      p.halo.visible = halo;
      if (halo) p.halo.alpha = 0.55 + Math.sin(now / 120) * 0.25;
      const bubble = p.full && now < p.bubbleUntil;
      p.bubble.visible = p.bubbleBg.visible = bubble;
    }

    for (let i = particles.length - 1; i >= 0; i--) {
      const p = particles[i];
      const s = p.root;
      p.age += dt;
      if (p.type === 'fall') s.x = p.baseX + Math.sin(p.age * 1.6 + p.phase) * p.sway;
      else s.x += p.vx * dt;
      s.y += p.vy * dt;
      s.rotation += (p.vRot || 0) * dt;
      if (p.type === 'smoke') { s.alpha = 0.32 * (1 - p.age / p.life); s.scale.set(s.scale.x * 1.008); }
      else if (p.type === 'spark') { s.alpha = Math.max(0, 1 - p.age / p.life); p.vy += 220 * dt; }
      else if (p.type === 'fall') s.alpha = Math.max(0, Math.min(1, (p.life - p.age) / 1.5));
      else s.alpha = Math.max(0, 1 - (p.age / p.life) * 0.9);
      if (p.age >= p.life || s.y > 1800) {
        s.removeFromParent();
        particles.splice(i, 1);
        particlePool.push(p);
      }
    }

    if (now >= comboUntil && el('combo-badge').classList.contains('active')) el('combo-badge').classList.remove('active');
    if (!festival && el('festival-overlay').classList.contains('active')) el('festival-overlay').classList.remove('active');
  });

  // Chạy 24/7: WebSocket im quá 60 giây (server luôn gửi nhịp 15 giây), mất WebGL hoặc ticker đứng thì tải lại trang
  // (giữ nguyên query string), nhưng chỉ khi server còn trả lời để không kẹt ở trang lỗi của trình duyệt.
  let contextLostAt = 0, reloading = false;
  app.canvas.addEventListener('webglcontextlost', () => { contextLostAt = Date.now(); });
  app.canvas.addEventListener('webglcontextrestored', () => { contextLostAt = 0; });
  async function reloadPage(reason) {
    if (reloading || disposed) return;
    reloading = true;
    try {
      const response = await fetch(location.href, { method: 'HEAD', cache: 'no-store', signal: AbortSignal.timeout(4000) });
      if (response.ok) { console.warn('Tải lại trang:', reason); location.reload(); return; }
    } catch { }
    reloading = false;
  }
  const watchdog = setInterval(() => {
    if (Date.now() - lastMessage > 60000) reloadPage('WebSocket im lặng quá 60 giây');
    else if (contextLostAt && Date.now() - contextLostAt > 5000) reloadPage('Mất WebGL context');
    else if (document.visibilityState === 'visible' && performance.now() - lastTick > 15000) reloadPage('Ticker Pixi đã dừng');
  }, 5000);

  window.addEventListener('beforeunload', () => {
    disposed = true; clearTimeout(reconnectTimer); clearInterval(watchdog); avatarQueue.length = 0;
    socket?.close(); bgmAudio?.pause(); currentAudio?.pause(); audioContext?.close();
    for (const p of viewers.values()) clearAvatar(p);
    app.destroy(true, { children: true });
  });

  updateSoundButton();
  connectWebSocket();
  // Cửa sổ phát không có nút bật tiếng: tự bật (Edge mở với --autoplay-policy); nếu trình duyệt vẫn chặn thì bật ở lần bấm đầu tiên.
  if (obs && !await setAudio(true)) {
    const unlock = async () => { if (await setAudio(true)) { window.removeEventListener('pointerdown', unlock); window.removeEventListener('keydown', unlock); } };
    window.addEventListener('pointerdown', unlock);
    window.addEventListener('keydown', unlock);
  }
})();
