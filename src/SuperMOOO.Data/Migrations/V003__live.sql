-- Livestream bán hàng (S20): chỉ thêm bảng mới, không đụng bảng cũ. clip_path: clip khớp môi dựng sẵn (GĐ4).
CREATE TABLE IF NOT EXISTS live_product(id TEXT PRIMARY KEY, name TEXT NOT NULL, facts TEXT NOT NULL DEFAULT '', script TEXT NOT NULL DEFAULT '',
  clip_path TEXT, updated_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS live_faq(id TEXT PRIMARY KEY, product_id TEXT NOT NULL REFERENCES live_product(id) ON DELETE CASCADE,
  question TEXT NOT NULL, answer TEXT NOT NULL, clip_path TEXT, updated_at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_live_faq_product ON live_faq(product_id);
