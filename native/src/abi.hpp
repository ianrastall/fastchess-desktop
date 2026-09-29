// Helpers for the C ABI entry points: every function catches all exceptions and converts
// them to fcd_status plus a thread-local message.
#pragma once

#include <new>
#include <string>

#include "fcd/fcd.h"
#include "util.hpp"

namespace fcd::abi {

template <typename F>
fcd_status guarded(F&& body) {
    try {
        body();
        set_last_error({});
        return FCD_OK;
    } catch (const Error& e) {
        set_last_error(e.what());
        return e.status();
    } catch (const std::bad_alloc&) {
        set_last_error("out of memory");
        return FCD_ERR_INTERNAL;
    } catch (const std::exception& e) {
        set_last_error(e.what());
        return FCD_ERR_INTERNAL;
    } catch (...) {
        set_last_error("unknown error");
        return FCD_ERR_INTERNAL;
    }
}

inline void require(const void* p, const char* what) {
    if (!p) throw Error(FCD_ERR_ARGUMENT, std::string(what) + " is NULL");
}

// Stores a string result for the caller (released with fcd_free).
inline void give(char** out, const std::string& text) {
    require(out, "output pointer");
    *out = nullptr;
    *out = dup_for_caller(text);
}

}  // namespace fcd::abi
