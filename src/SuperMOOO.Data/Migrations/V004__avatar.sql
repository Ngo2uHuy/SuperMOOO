-- Avatar khớp môi (S19): thư viện video avatar; toạ độ mặt đã dò nằm cạnh file avatar (lipsync\avatars\<id>).
CREATE TABLE IF NOT EXISTS avatar(id TEXT PRIMARY KEY, name TEXT NOT NULL, source_path TEXT NOT NULL, normalized_path TEXT NOT NULL,
  face_status TEXT NOT NULL DEFAULT 'none', face_seconds INTEGER NOT NULL DEFAULT 0, duration_sec REAL NOT NULL DEFAULT 0, created_at INTEGER NOT NULL);
