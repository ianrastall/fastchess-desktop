// Shared helpers: error propagation, UTF-8 handling, JSON/XML escaping.
#pragma once

#include <cstdint>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include "fcd/fcd.h"

namespace fcd {

// Thrown inside the library; converted to fcd_status at the C boundary.
class Error : public std::runtime_error {
   public:
    Error(fcd_status status, const std::string& message) : std::runtime_error(message), status_(status) {}
    fcd_status status() const noexcept { return status_; }

   private:
    fcd_status status_;
};

void set_last_error(std::string message);
const char* last_error();

// Converts a UTF-8 path string to a filesystem path without code-page loss on Windows.
std::filesystem::path utf8_path(std::string_view utf8);

// Returns the input if it is valid UTF-8; otherwise decodes it as Windows-1252.
std::string to_utf8_lenient(std::string_view text);

std::string trim(std::string_view text);
std::vector<std::string> split(std::string_view text, char separator);
std::string join(const std::vector<std::string>& parts, char separator);

bool iequals(std::string_view a, std::string_view b);

// Appends a quoted JSON string literal.
void json_string(std::string& out, std::string_view text);

// Escapes text for XML element content or attribute values, dropping
// characters that are not allowed in XML 1.0.
std::string xml_escape(std::string_view text);

// Returns true if text is a single syntactically valid JSON value.
bool json_is_valid(std::string_view text);

// 64-bit FNV-1a, formatted as 16 hex digits.
std::string fnv1a_hex(std::string_view text);

// Heap copy that the caller releases with fcd_free().
char* dup_for_caller(const std::string& text);

}  // namespace fcd
