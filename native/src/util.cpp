#include "util.hpp"

#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace fcd {

namespace {
thread_local std::string g_last_error;

// Windows-1252 code points for bytes 0x80..0x9F; other bytes map to Latin-1.
constexpr char32_t kCp1252High[32] = {
    0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160,
    0x2039, 0x0152, 0x008D, 0x017D, 0x008F, 0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022,
    0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178};

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

bool is_valid_utf8(std::string_view s) {
    size_t i = 0;
    while (i < s.size()) {
        const auto c = static_cast<unsigned char>(s[i]);
        size_t len = 0;
        char32_t cp = 0;
        if (c < 0x80) {
            ++i;
            continue;
        } else if ((c & 0xE0) == 0xC0) {
            len = 2;
            cp = c & 0x1F;
        } else if ((c & 0xF0) == 0xE0) {
            len = 3;
            cp = c & 0x0F;
        } else if ((c & 0xF8) == 0xF0) {
            len = 4;
            cp = c & 0x07;
        } else {
            return false;
        }
        if (i + len > s.size()) return false;
        for (size_t k = 1; k < len; ++k) {
            const auto cc = static_cast<unsigned char>(s[i + k]);
            if ((cc & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (cc & 0x3F);
        }
        if ((len == 2 && cp < 0x80) || (len == 3 && cp < 0x800) || (len == 4 && (cp < 0x10000 || cp > 0x10FFFF)) ||
            (cp >= 0xD800 && cp <= 0xDFFF))
            return false;
        i += len;
    }
    return true;
}

// Decodes one UTF-8 code point starting at i (input already validated).
char32_t decode_at(std::string_view s, size_t& i) {
    const auto c = static_cast<unsigned char>(s[i]);
    if (c < 0x80) {
        ++i;
        return c;
    }
    size_t len = (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : 4;
    char32_t cp = len == 2 ? (c & 0x1F) : len == 3 ? (c & 0x0F) : (c & 0x07);
    for (size_t k = 1; k < len && i + k < s.size(); ++k) cp = (cp << 6) | (static_cast<unsigned char>(s[i + k]) & 0x3F);
    i += len;
    return cp;
}

class JsonValidator {
   public:
    explicit JsonValidator(std::string_view s) : s_(s) {}

    bool run() {
        skip_ws();
        if (!value(0)) return false;
        skip_ws();
        return pos_ == s_.size();
    }

   private:
    std::string_view s_;
    size_t pos_ = 0;

    void skip_ws() {
        while (pos_ < s_.size() && (s_[pos_] == ' ' || s_[pos_] == '\t' || s_[pos_] == '\n' || s_[pos_] == '\r'))
            ++pos_;
    }
    bool literal(std::string_view word) {
        if (s_.substr(pos_, word.size()) != word) return false;
        pos_ += word.size();
        return true;
    }
    bool string() {
        if (pos_ >= s_.size() || s_[pos_] != '"') return false;
        ++pos_;
        while (pos_ < s_.size()) {
            const auto c = static_cast<unsigned char>(s_[pos_]);
            if (c == '"') {
                ++pos_;
                return true;
            }
            if (c < 0x20) return false;
            if (c == '\\') {
                ++pos_;
                if (pos_ >= s_.size()) return false;
                const char e = s_[pos_];
                if (e == 'u') {
                    for (int k = 0; k < 4; ++k) {
                        ++pos_;
                        if (pos_ >= s_.size() || !std::isxdigit(static_cast<unsigned char>(s_[pos_]))) return false;
                    }
                } else if (std::strchr("\"\\/bfnrt", e) == nullptr) {
                    return false;
                }
            }
            ++pos_;
        }
        return false;
    }
    bool number() {
        const size_t start = pos_;
        if (pos_ < s_.size() && s_[pos_] == '-') ++pos_;
        auto digits = [&] {
            const size_t b = pos_;
            while (pos_ < s_.size() && std::isdigit(static_cast<unsigned char>(s_[pos_]))) ++pos_;
            return pos_ > b;
        };
        if (!digits()) return false;
        if (pos_ < s_.size() && s_[pos_] == '.') {
            ++pos_;
            if (!digits()) return false;
        }
        if (pos_ < s_.size() && (s_[pos_] == 'e' || s_[pos_] == 'E')) {
            ++pos_;
            if (pos_ < s_.size() && (s_[pos_] == '+' || s_[pos_] == '-')) ++pos_;
            if (!digits()) return false;
        }
        return pos_ > start;
    }
    bool value(int depth) {
        if (depth > 256 || pos_ >= s_.size()) return false;
        const char c = s_[pos_];
        if (c == '{') {
            ++pos_;
            skip_ws();
            if (pos_ < s_.size() && s_[pos_] == '}') {
                ++pos_;
                return true;
            }
            for (;;) {
                skip_ws();
                if (!string()) return false;
                skip_ws();
                if (pos_ >= s_.size() || s_[pos_] != ':') return false;
                ++pos_;
                skip_ws();
                if (!value(depth + 1)) return false;
                skip_ws();
                if (pos_ < s_.size() && s_[pos_] == ',') {
                    ++pos_;
                    continue;
                }
                if (pos_ < s_.size() && s_[pos_] == '}') {
                    ++pos_;
                    return true;
                }
                return false;
            }
        }
        if (c == '[') {
            ++pos_;
            skip_ws();
            if (pos_ < s_.size() && s_[pos_] == ']') {
                ++pos_;
                return true;
            }
            for (;;) {
                skip_ws();
                if (!value(depth + 1)) return false;
                skip_ws();
                if (pos_ < s_.size() && s_[pos_] == ',') {
                    ++pos_;
                    continue;
                }
                if (pos_ < s_.size() && s_[pos_] == ']') {
                    ++pos_;
                    return true;
                }
                return false;
            }
        }
        if (c == '"') return string();
        if (c == 't') return literal("true");
        if (c == 'f') return literal("false");
        if (c == 'n') return literal("null");
        return number();
    }
};
}  // namespace

void set_last_error(std::string message) { g_last_error = std::move(message); }

const char* last_error() { return g_last_error.c_str(); }

std::filesystem::path utf8_path(std::string_view utf8) {
    std::u8string u8(utf8.begin(), utf8.end());
    return std::filesystem::path(u8);
}

std::string to_utf8_lenient(std::string_view text) {
    if (is_valid_utf8(text)) return std::string(text);
    std::string out;
    out.reserve(text.size() + 8);
    for (const char ch : text) {
        const auto c = static_cast<unsigned char>(ch);
        if (c < 0x80)
            out += ch;
        else if (c < 0xA0)
            append_utf8(out, kCp1252High[c - 0x80]);
        else
            append_utf8(out, c);
    }
    return out;
}

std::string trim(std::string_view text) {
    size_t b = 0, e = text.size();
    while (b < e && std::isspace(static_cast<unsigned char>(text[b]))) ++b;
    while (e > b && std::isspace(static_cast<unsigned char>(text[e - 1]))) --e;
    return std::string(text.substr(b, e - b));
}

std::vector<std::string> split(std::string_view text, char separator) {
    std::vector<std::string> parts;
    size_t start = 0;
    for (;;) {
        const size_t pos = text.find(separator, start);
        if (pos == std::string_view::npos) {
            parts.emplace_back(text.substr(start));
            break;
        }
        parts.emplace_back(text.substr(start, pos - start));
        start = pos + 1;
    }
    return parts;
}

std::string join(const std::vector<std::string>& parts, char separator) {
    std::string out;
    for (size_t i = 0; i < parts.size(); ++i) {
        if (i) out += separator;
        out += parts[i];
    }
    return out;
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
        if (std::tolower(static_cast<unsigned char>(a[i])) != std::tolower(static_cast<unsigned char>(b[i])))
            return false;
    return true;
}

void json_string(std::string& out, std::string_view text) {
    out += '"';
    for (const char ch : text) {
        const auto c = static_cast<unsigned char>(ch);
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof buf, "\\u%04x", c);
                    out += buf;
                } else {
                    out += ch;
                }
        }
    }
    out += '"';
}

std::string xml_escape(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    size_t i = 0;
    while (i < text.size()) {
        const size_t start = i;
        const char32_t cp = decode_at(text, i);
        const bool allowed = cp == 0x9 || cp == 0xA || cp == 0xD || (cp >= 0x20 && cp <= 0xD7FF) ||
                             (cp >= 0xE000 && cp <= 0xFFFD) || (cp >= 0x10000 && cp <= 0x10FFFF);
        if (!allowed) continue;
        switch (cp) {
            case '&': out += "&amp;"; break;
            case '<': out += "&lt;"; break;
            case '>': out += "&gt;"; break;
            case '"': out += "&quot;"; break;
            case '\'': out += "&apos;"; break;
            default: out.append(text.substr(start, i - start));
        }
    }
    return out;
}

bool json_is_valid(std::string_view text) { return JsonValidator(text).run(); }

std::string fnv1a_hex(std::string_view text) {
    std::uint64_t h = 1469598103934665603ULL;
    for (const char c : text) {
        h ^= static_cast<unsigned char>(c);
        h *= 1099511628211ULL;
    }
    char buf[17];
    std::snprintf(buf, sizeof buf, "%016llx", static_cast<unsigned long long>(h));
    return buf;
}

char* dup_for_caller(const std::string& text) {
    auto* p = static_cast<char*>(std::malloc(text.size() + 1));
    if (!p) throw Error(FCD_ERR_INTERNAL, "out of memory");
    std::memcpy(p, text.data(), text.size());
    p[text.size()] = '\0';
    return p;
}

}  // namespace fcd
