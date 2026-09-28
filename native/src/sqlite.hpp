// Minimal RAII wrapper over the SQLite C API.
#pragma once

#include <sqlite3.h>

#include <cstdint>
#include <optional>
#include <string>

#include "util.hpp"

namespace fcd {

class Statement {
   public:
    Statement(sqlite3* db, const std::string& sql) : db_(db) {
        if (sqlite3_prepare_v2(db, sql.c_str(), -1, &stmt_, nullptr) != SQLITE_OK)
            throw Error(FCD_ERR_DATABASE, std::string(sqlite3_errmsg(db)) + " in: " + sql);
    }
    ~Statement() { sqlite3_finalize(stmt_); }
    Statement(const Statement&) = delete;
    Statement& operator=(const Statement&) = delete;

    Statement& bind(int index, const std::string& value) {
        check(sqlite3_bind_text(stmt_, index, value.data(), static_cast<int>(value.size()), SQLITE_TRANSIENT));
        return *this;
    }
    Statement& bind(int index, std::int64_t value) {
        check(sqlite3_bind_int64(stmt_, index, value));
        return *this;
    }
    Statement& bind(int index, double value) {
        check(sqlite3_bind_double(stmt_, index, value));
        return *this;
    }
    Statement& bind(int index, const std::optional<int>& value) {
        check(value ? sqlite3_bind_int64(stmt_, index, *value) : sqlite3_bind_null(stmt_, index));
        return *this;
    }
    Statement& bind_null(int index) {
        check(sqlite3_bind_null(stmt_, index));
        return *this;
    }
    // Binds NULL for an empty string.
    Statement& bind_text_or_null(int index, const std::string& value) {
        return value.empty() ? bind_null(index) : bind(index, value);
    }

    // Returns true when a row is available.
    bool step() {
        const int rc = sqlite3_step(stmt_);
        if (rc == SQLITE_ROW) return true;
        if (rc == SQLITE_DONE) return false;
        throw Error(FCD_ERR_DATABASE, sqlite3_errmsg(db_));
    }
    void run() {
        step();
        reset();
    }
    void reset() {
        sqlite3_reset(stmt_);
        sqlite3_clear_bindings(stmt_);
    }

    bool is_null(int col) const { return sqlite3_column_type(stmt_, col) == SQLITE_NULL; }
    std::int64_t i64(int col) const { return sqlite3_column_int64(stmt_, col); }
    double f64(int col) const { return sqlite3_column_double(stmt_, col); }
    std::optional<int> opt_int(int col) const {
        return is_null(col) ? std::nullopt : std::optional<int>(sqlite3_column_int(stmt_, col));
    }
    std::string text(int col) const {
        const auto* p = reinterpret_cast<const char*>(sqlite3_column_text(stmt_, col));
        return p ? std::string(p, static_cast<size_t>(sqlite3_column_bytes(stmt_, col))) : std::string();
    }

   private:
    void check(int rc) {
        if (rc != SQLITE_OK) throw Error(FCD_ERR_DATABASE, sqlite3_errmsg(db_));
    }
    sqlite3* db_;
    sqlite3_stmt* stmt_ = nullptr;
};

inline void exec(sqlite3* db, const std::string& sql) {
    char* msg = nullptr;
    if (sqlite3_exec(db, sql.c_str(), nullptr, nullptr, &msg) != SQLITE_OK) {
        std::string text = msg ? msg : "unknown SQLite error";
        sqlite3_free(msg);
        throw Error(FCD_ERR_DATABASE, text);
    }
}

// Rolls back unless commit() was called.
class Transaction {
   public:
    explicit Transaction(sqlite3* db) : db_(db) { exec(db_, "BEGIN IMMEDIATE"); }
    ~Transaction() {
        if (!done_) sqlite3_exec(db_, "ROLLBACK", nullptr, nullptr, nullptr);
    }
    void commit() {
        exec(db_, "COMMIT");
        done_ = true;
    }

   private:
    sqlite3* db_;
    bool done_ = false;
};

}  // namespace fcd
