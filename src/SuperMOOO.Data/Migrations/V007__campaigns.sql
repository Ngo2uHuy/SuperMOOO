-- Chiến dịch: mức "Chỉ gợi ý" (planner không tạo bài). Cột automation có CHECK chỉ 2 giá trị nên dùng cờ riêng.
ALTER TABLE pub_campaigns ADD COLUMN suggest_only INTEGER NOT NULL DEFAULT 0;
