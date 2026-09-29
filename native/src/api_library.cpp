// C ABI entry points for the library itself: versions, errors, memory.
#include <cstdlib>

#include "fcd/fcd.h"
#include "util.hpp"

#ifndef FCD_VERSION_STRING
#define FCD_VERSION_STRING "0.0.0"
#endif

extern "C" {

FCD_API int32_t fcd_abi_version(void) { return FCD_ABI_VERSION; }

FCD_API const char* fcd_version(void) { return FCD_VERSION_STRING; }

FCD_API const char* fcd_last_error(void) { return fcd::last_error(); }

FCD_API void fcd_free(void* p) { std::free(p); }

}  // extern "C"
