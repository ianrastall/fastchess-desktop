cmake_minimum_required(VERSION 3.25)

project(fcd_core VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

option(FCD_BUILD_TESTS "Build the native test executable" ON)

# SQLite comes from vcpkg on Windows (see vcpkg.json) or from the system elsewhere.
find_package(unofficial-sqlite3 CONFIG QUIET)
if(unofficial-sqlite3_FOUND)
    set(FCD_SQLITE_TARGET unofficial::sqlite3::sqlite3)
else()
    find_package(SQLite3 REQUIRED)
    set(FCD_SQLITE_TARGET SQLite::SQLite3)
endif()

add_library(fcd_core SHARED
    src/api.cpp
    src/chess_rules.cpp
    src/database.cpp
    src/exporters.cpp
    src/game.cpp
    src/openings.cpp
    src/util.cpp
)
target_include_directories(fcd_core
    PUBLIC include
    PRIVATE src third_party/chess-library)
target_compile_definitions(fcd_core PRIVATE
    FCD_BUILDING
    FCD_VERSION_STRING="${PROJECT_VERSION}")
target_link_libraries(fcd_core PRIVATE ${FCD_SQLITE_TARGET})

if(MSVC)
    target_compile_options(fcd_core PRIVATE /W4 /utf-8 /permissive- /Zc:__cplusplus /EHsc)
    # Static CRT so the DLL does not depend on a separately installed VC++ runtime.
    set_property(TARGET fcd_core PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    target_compile_options(fcd_core PRIVATE -Wall -Wextra)
endif()

if(FCD_BUILD_TESTS)
    enable_testing()
    add_executable(fcd_tests tests/fcd_tests.cpp)
    target_link_libraries(fcd_tests PRIVATE fcd_core)
    target_compile_definitions(fcd_tests PRIVATE
        FCD_TEST_DATA_DIR="${CMAKE_CURRENT_SOURCE_DIR}/tests/data"
        FCD_TEST_OPENINGS_TSV="${CMAKE_CURRENT_SOURCE_DIR}/../assets/lichess-openings/lichess-openings.tsv")
    if(MSVC)
        target_compile_options(fcd_tests PRIVATE /utf-8 /EHsc)
        set_property(TARGET fcd_tests PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
    add_test(NAME fcd_tests COMMAND fcd_tests)
endif()

install(TARGETS fcd_core RUNTIME DESTINATION . LIBRARY DESTINATION .)
