-- Лабораторна 2: фрагмент "відгуки" у PostgreSQL.
-- Виконувати в базі library (pgAdmin -> Query Tool -> F5), після library_db.sql.
-- Той самий фрагмент у MongoDB зберігається як один документ, тут він розбитий на 3 таблиці.

CREATE TABLE reviews (
    id               BIGINT PRIMARY KEY,            -- id задає застосунок (однаковий у SQL та Mongo)
    book_id          INT NOT NULL REFERENCES books(id),
    member_id        INT NOT NULL REFERENCES members(id),
    rating           SMALLINT NOT NULL CHECK (rating BETWEEN 1 AND 5),
    title            VARCHAR(150) NOT NULL,
    body             TEXT NOT NULL,
    reading_progress SMALLINT NOT NULL CHECK (reading_progress BETWEEN 0 AND 100),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE review_tags (
    review_id BIGINT NOT NULL REFERENCES reviews(id),
    tag       VARCHAR(50) NOT NULL,
    PRIMARY KEY (review_id, tag)
);

CREATE TABLE review_comments (
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    review_id  BIGINT NOT NULL REFERENCES reviews(id),
    member_id  INT NOT NULL REFERENCES members(id),
    text       TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Індекси під ті самі запити, що й у MongoDB (щоб порівняння було чесним)
CREATE INDEX ix_reviews_book_created   ON reviews (book_id, created_at DESC);
CREATE INDEX ix_reviews_created        ON reviews (created_at DESC);
CREATE INDEX ix_review_tags_tag        ON review_tags (tag, review_id);
CREATE INDEX ix_review_comments_review ON review_comments (review_id);

-- Тестові дані для бенчмарку: 1000 книг і 2000 читачів,
-- щоб відгуки було до кого прив'язувати.
INSERT INTO books (publisher_id, category_id, title, publish_year, pages)
SELECT 1, 1, 'Тестова книга ' || g, 2000 + (g % 25), 100 + (g % 400)
FROM generate_series(1, 1000) AS g;

INSERT INTO members (membership_type_id, card_number, full_name)
SELECT 1, 'BM-' || g, 'Читач ' || g
FROM generate_series(1, 2000) AS g;
