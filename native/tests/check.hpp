// Minimal check macros shared by the native test files. Uses only the public header.
#pragma once

#include <cmath>
#include <cstdio>
#include <string>

#include "fcd/fcd.h"

namespace fcd_test {

inline int g_failures = 0;
inline int g_checks = 0;

inline std::string take(char* p) {
    std::string s = p ? p : "";
    fcd_free(p);
    return s;
}

inline bool contains(const std::string& haystack, const std::string& needle) {
    return haystack.find(needle) != std::string::npos;
}

inline bool near(double a, double b, double tolerance) { return std::fabs(a - b) <= tolerance; }

}  // namespace fcd_test

#define CHECK(cond)                                                                        \
    do {                                                                                   \
        ++fcd_test::g_checks;                                                              \
        if (!(cond)) {                                                                     \
            ++fcd_test::g_failures;                                                        \
            std::fprintf(stderr, "%s:%d: CHECK failed: %s\n", __FILE__, __LINE__, #cond);  \
        }                                                                                  \
    } while (0)

#define CHECK_OK(expr)                                                                          \
    do {                                                                                        \
        ++fcd_test::g_checks;                                                                   \
        const fcd_status st_ = (expr);                                                          \
        if (st_ != FCD_OK) {                                                                    \
            ++fcd_test::g_failures;                                                             \
            std::fprintf(stderr, "%s:%d: %s returned %d: %s\n", __FILE__, __LINE__, #expr, st_, \
                         fcd_last_error());                                                     \
        }                                                                                       \
    } while (0)

// Test groups defined in the other test files.
void run_tools_tests();
void run_process_tests();
