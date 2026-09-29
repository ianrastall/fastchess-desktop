// Minimal JSON document model: parsing input that crosses the C ABI (settings, name lists)
// and writing small results. Output helpers for strings live in util.hpp.
#pragma once

#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace fcd::json {

class Value {
   public:
    enum class Type { Null, Bool, Number, String, Array, Object };

    Value() = default;

    // Throws fcd::Error(FCD_ERR_PARSE) on malformed input.
    static Value parse(std::string_view text);

    Type type() const { return type_; }
    bool is_null() const { return type_ == Type::Null; }
    bool is_string() const { return type_ == Type::String; }
    bool is_array() const { return type_ == Type::Array; }
    bool is_object() const { return type_ == Type::Object; }

    // Typed access; a value of another type yields the fallback.
    bool as_bool(bool fallback = false) const { return type_ == Type::Bool ? bool_ : fallback; }
    double as_number(double fallback = 0) const { return type_ == Type::Number ? number_ : fallback; }
    const std::string& as_string() const;
    const std::vector<Value>& items() const;                                // array elements
    const std::vector<std::pair<std::string, Value>>& members() const;       // object members, in order

    // Object member lookup; null when absent or when this is not an object.
    const Value* find(std::string_view key) const;

    // Member access with fallbacks (for settings objects with optional fields).
    std::string get_string(std::string_view key, std::string_view fallback = {}) const;
    double get_number(std::string_view key, double fallback = 0) const;
    long long get_integer(std::string_view key, long long fallback = 0) const;
    bool get_bool(std::string_view key, bool fallback = false) const;

    // Array of strings; non-string elements are skipped.
    std::vector<std::string> string_items() const;

   private:
    friend class Parser;
    Type type_ = Type::Null;
    bool bool_ = false;
    double number_ = 0;
    std::string string_;
    std::vector<Value> array_;
    std::vector<std::pair<std::string, Value>> object_;
};

// Shortest text that round-trips the value ("0.05", "2300", "2.5"), as .NET formats doubles.
std::string number(double value);

// ["a","b"] with JSON escaping.
std::string string_array(const std::vector<std::string>& items);

}  // namespace fcd::json
