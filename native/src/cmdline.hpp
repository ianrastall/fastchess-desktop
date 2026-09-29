// Windows command-line quoting and splitting (CommandLineToArgvW rules). Used for display,
// for user-typed extra arguments, and to build the command line CreateProcessW receives.
#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace fcd::cmdline {

// Quotes one argument where required ("with space" -> "\"with space\"").
std::string quote(std::string_view argument);

// Joins arguments into one command line, quoting where required.
std::string format(const std::vector<std::string>& arguments);

// Splits user-typed text such as "-t tags.txt --plycount" into arguments.
std::vector<std::string> split(std::string_view text);

}  // namespace fcd::cmdline
