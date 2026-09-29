#include "cmdline.hpp"

namespace fcd::cmdline {

namespace {
bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r'; }
}  // namespace

std::string quote(std::string_view argument) {
    if (!argument.empty() && argument.find_first_of(" \t\n\v\"") == std::string_view::npos) return std::string(argument);

    std::string out = "\"";
    size_t backslashes = 0;
    for (const char c : argument) {
        if (c == '\\') {
            ++backslashes;
            continue;
        }
        // Backslashes before a quote are doubled and the quote is escaped; elsewhere they are literal.
        out.append(c == '"' ? backslashes * 2 + 1 : backslashes, '\\');
        backslashes = 0;
        out += c;
    }
    out.append(backslashes * 2, '\\');  // before the closing quote
    out += '"';
    return out;
}

std::string format(const std::vector<std::string>& arguments) {
    std::string out;
    for (size_t i = 0; i < arguments.size(); ++i) {
        if (i) out += ' ';
        out += quote(arguments[i]);
    }
    return out;
}

std::vector<std::string> split(std::string_view text) {
    std::vector<std::string> result;
    std::string current;
    bool in_quotes = false;
    bool has_token = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (c == '\\') {
            const size_t start = i;
            while (i < text.size() && text[i] == '\\') ++i;
            const size_t count = i - start;
            if (i < text.size() && text[i] == '"') {
                current.append(count / 2, '\\');
                if (count % 2 == 1) current += '"';
                else in_quotes = !in_quotes;
            } else {
                current.append(count, '\\');
                --i;
            }
            has_token = true;
        } else if (c == '"') {
            in_quotes = !in_quotes;
            has_token = true;
        } else if (is_space(c) && !in_quotes) {
            if (has_token) result.push_back(current);
            current.clear();
            has_token = false;
        } else {
            current += c;
            has_token = true;
        }
    }
    if (has_token) result.push_back(current);
    return result;
}

}  // namespace fcd::cmdline
