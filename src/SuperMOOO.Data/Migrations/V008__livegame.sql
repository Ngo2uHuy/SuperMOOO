CREATE TABLE livegame_session (
    id TEXT PRIMARY KEY,
    started_at INTEGER NOT NULL,
    ended_at INTEGER,
    incense INTEGER NOT NULL DEFAULT 0,
    prayers INTEGER NOT NULL DEFAULT 0,
    likes INTEGER NOT NULL DEFAULT 0,
    simulated INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE livegame_donor (
    session_id TEXT NOT NULL REFERENCES livegame_session(id),
    user_id TEXT NOT NULL,
    nickname TEXT NOT NULL,
    coins INTEGER NOT NULL,
    day TEXT NOT NULL,
    PRIMARY KEY (session_id, user_id, day)
);
CREATE INDEX ix_livegame_session_started ON livegame_session(started_at);
CREATE INDEX ix_livegame_donor_day ON livegame_donor(day);
