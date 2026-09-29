#include "json.hpp"

#include <charconv>
#include <cmath>
#include <cstdlib>

#include "util.hpp"

namespace fcd::json {

namespace {

const std::string kEmptyString;
const std::vector<Value> kEmptyArray;
const std::vector<std::pair<std::string, Value>> kEmptyObject;

void append_utf8(std::string& out, char32_t cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (cp >> 18));
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

}  // namespace

class Parser {
   public:
    explicit Parser(std::string_view s) : s_(s) {}

    Value run() {
        skip_ws();
        Value v = value(0);
        skip_ws();
        if (pos_ != s_.size()) fail("unexpected text after the value");
        return v;
    }

   private:
    std::string_view s_;
    size_t pos_ = 0;

    [[noreturn]] void fail(const std::string& what) const {
        throw Error(FCD_ERR_PARSE, "invalid JSON at offset " + std::to_string(pos_) + ": " + what);
    }

    void skip_ws() {
        while (pos_ < s_.size() && (s_[pos_] == ' ' || s_[pos_] == '\t' || s_[pos_] == '\n' || s_[pos_] == '\r'))
            ++pos_;
    }

    char peek() const { return pos_ < s_.size() ? s_[pos_] : '\0'; }

    void expect(char c) {
        if (peek() != c) fail(std::string("expected '") + c + "'");
        ++pos_;
    }

    unsigned hex4() {
        if (pos_ + 4 > s_.size()) fail("truncated \\u escape");
        unsigned v = 0;
        for (int k = 0; k < 4; ++k) {
            const char c = s_[pos_++];
            v <<= 4;
            if (c >= '0' && c <= '9') v |= static_cast<unsigned>(c - '0');
            else if (c >= 'a' && c <= 'f') v |= static_cast<unsigned>(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') v |= static_cast<unsigned>(c - 'A' + 10);
            else fail("bad \\u escape");
        }
        return v;
    }

    std::string string() {
        expect('"');
        std::string out;
        while (pos_ < s_.size()) {
            const char c = s_[pos_++];
            if (c == '"') return out;
            if (static_cast<unsigned char>(c) < 0x20) fail("control character in string");
            if (c != '\\') {
                out += c;
                continue;
            }
            if (pos_ >= s_.size()) break;
            const char e = s_[pos_++];
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    char32_t cp = hex4();
                    if (cp >= 0xD800 && cp <= 0xDBFF && s_.substr(pos_, 2) == "\\u") {
                        pos_ += 2;
                        const unsigned low = hex4();
                        if (low < 0xDC00 || low > 0xDFFF) fail("unpaired surrogate");
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    } else if (cp >= 0xD800 && cp <= 0xDFFF) {
                        fail("unpaired surrogate");
                    }
                    append_utf8(out, cp);
                    break;
                }
                default: fail("bad escape");
            }
        }
        fail("unterminated string");
    }

    Value value(int depth) {
        if (depth > 256) fail("nesting too deep");
        Value v;
        const char c = peek();
        if (c == '{') {
            ++pos_;
            v.type_ = Value::Type::Object;
            skip_ws();
            if (peek() == '}') {
                ++pos_;
                return v;
            }
            for (;;) {
                skip_ws();
                std::string key = string();
                skip_ws();
                expect(':');
                skip_ws();
                v.object_.emplace_back(std::move(key), value(depth + 1));
                skip_ws();
                if (peek() == ',') {
                    ++pos_;
                    continue;
                }
                expect('}');
                return v;
            }
        }
        if (c == '[') {
            ++pos_;
            v.type_ = Value::Type::Array;
            skip_ws();
            if (peek() == ']') {
                ++pos_;
                return v;
            }
            for (;;) {
                skip_ws();
                v.array_.push_back(value(depth + 1));
                skip_ws();
                if (peek() == ',') {
                    ++pos_;
                    continue;
                }
                expect(']');
                return v;
            }
        }
        if (c == '"') {
            v.type_ = Value::Type::String;
            v.string_ = string();
            return v;
        }
        if (s_.substr(pos_, 4) == "true") {
            pos_ += 4;
            v.type_ = Value::Type::Bool;
            v.bool_ = true;
            return v;
        }
        if (s_.substr(pos_, 5) == "false") {
            pos_ += 5;
            v.type_ = Value::Type::Bool;
            return v;
        }
        if (s_.substr(pos_, 4) == "null") {
            pos_ += 4;
            return v;
        }
        // Number: validate the JSON grammar, then convert.
        const size_t start = pos_;
        if (peek() == '-') ++pos_;
        auto digits = [&] {
            const size_t b = pos_;
            while (pos_ < s_.size() && s_[pos_] >= '0' && s_[pos_] <= '9') ++pos_;
            return pos_ > b;
        };
        if (!digits()) fail("unexpected character");
        if (peek() == '.') {
            ++pos_;
            if (!digits()) fail("bad number");
        }
        if (peek() == 'e' || peek() == 'E') {
            ++pos_;
            if (peek() == '+' || peek() == '-') ++pos_;
            if (!digits()) fail("bad number");
        }
        // from_chars is locale-independent ('.' is always the decimal point).
        const char* first = s_.data() + start;
        if (*first == '-') ++first;  // from_chars accepts no sign for the magnitude we negate below
        double magnitude = 0;
        const auto r = std::from_chars(first, s_.data() + pos_, magnitude);
        if (r.ec == std::errc::invalid_argument) fail("bad number");
        v.type_ = Value::Type::Number;
        v.number_ = s_[start] == '-' ? -magnitude : magnitude;
        return v;
    }
};

Value Value::parse(std::string_view text) { return Parser(text).run(); }

const std::string& Value::as_string() const { return type_ == Type::String ? string_ : kEmptyString; }

const std::vector<Value>& Value::items() const { return type_ == Type::Array ? array_ : kEmptyArray; }

const std::vector<std::pair<std::string, Value>>& Value::members() const {
    return type_ == Type::Object ? object_ : kEmptyObject;
}

const Value* Value::find(std::string_view key) const {
    if (type_ != Type::Object) return nullptr;
    for (const auto& [k, v] : object_)
        if (k == key) return &v;
    return nullptr;
}

std::string Value::get_string(std::string_view key, std::string_view fallback) const {
    const Value* v = find(key);
    return v && v->is_string() ? v->string_ : std::string(fallback);
}

double Value::get_number(std::string_view key, double fallback) const {
    const Value* v = find(key);
    return v && v->type_ == Type::Number ? v->number_ : fallback;
}

long long Value::get_integer(std::string_view key, long long fallback) const {
    const Value* v = find(key);
    return v && v->type_ == Type::Number ? static_cast<long long>(std::llround(v->number_)) : fallback;
}

bool Value::get_bool(std::string_view key, bool fallback) const {
    const Value* v = find(key);
    return v && v->type_ == Type::Bool ? v->bool_ : fallback;
}

std::vector<std::string> Value::string_items() const {
    std::vector<std::string> out;
    for (const auto& item : items())
        if (item.is_string()) out.push_back(item.string_);
    return out;
}

std::string number(double value) {
    if (!std::isfinite(value)) return "0";
    char buf[64];
    const auto r = std::to_chars(buf, buf + sizeof buf, value);
    return std::string(buf, r.ptr);
}

std::string string_array(const std::vector<std::string>& items) {
    std::string out = "[";
    for (size_t i = 0; i < items.size(); ++i) {
        if (i) out += ',';
        json_string(out, items[i]);
    }
    return out + "]";
}

}  // namespace fcd::json
