-- Danh sách tác vụ (ORDER BY created_at DESC LIMIT) và lọc theo trạng thái ở S15; retention lọc status + created_at.
CREATE INDEX ix_job_created ON job(created_at);
CREATE INDEX ix_job_status ON job(status, created_at);
-- Khoá ngoại trỏ về job: thiếu index thì mỗi job bị xoá phải quét cả bảng con (CASCADE / SET NULL).
CREATE INDEX ix_job_parent ON job(parent_id);
CREATE INDEX ix_media_job ON media_file(job_id);
CREATE INDEX ix_crawl_run_job ON crawl_run(job_id);
