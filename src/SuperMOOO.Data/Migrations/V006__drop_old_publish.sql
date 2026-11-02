-- Bảng đăng bài cũ (V001) đã được thay bằng pub_* (V005); không còn code đọc/ghi. Index ix_post_* đi theo bảng.
-- Bảng con (publish_post) xoá trước để không vướng khoá ngoại tới publish_account.
DROP TABLE IF EXISTS publish_post;
DROP TABLE IF EXISTS publish_account;
