export const platforms = {
  facebook: { name: 'Facebook', layout: 'landscape' },
  youtube: { name: 'YouTube', layout: 'landscape' },
  tiktok: { name: 'TikTok', layout: 'portrait' },
};

export const layouts = {
  landscape: { width: 1920, height: 1080, label: 'Ngang · 16:9' },
  portrait: { width: 1080, height: 1920, label: 'Dọc · 9:16' },
};

// Khung mỗi nền tảng tự có mặc định; không lưu token hay stream key ở đây.
export function normalizeProfile(input = {}) {
  const platform = Object.hasOwn(platforms, input?.platform) ? input.platform : 'tiktok';
  const layout = Object.hasOwn(layouts, input?.layout) ? input.layout : platforms[platform].layout;
  return { platform, layout, fit: input?.fit === 'cover' ? 'cover' : 'contain' };
}

export function profileFromSearch(search) {
  return normalizeProfile(Object.fromEntries(new URLSearchParams(search)));
}
