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
    src/analysis.cpp
    src/api.cpp
    src/api_library.cpp
    src/api_process.cpp
    src/api_tools.cpp
    src/chess_rules.cpp
    src/cmdline.cpp
    src/database.cpp
    src/exporters.cpp
    src/fastchess.cpp
    src/formats.cpp
    src/game.cpp
    src/json.cpp
    src/openings.cpp
    src/process.cpp
    src/ratings.cpp
    src/stats.cpp
    src/tools.cpp
    src/tournament.cpp
    src/uci.cpp
    src/util.cpp
)
target_include_directories(fcd_core
    PUBLIC include
    PRIVATE src third_party/chess-library)
target_compile_definitions(fcd_core PRIVATE
    FCD_BUILDING
    FCD_VERSION_STRING="${PROJECT_VERSION}")
find_package(Threads REQUIRED)
target_link_libraries(fcd_core PRIVATE ${FCD_SQLITE_TARGET} Threads::Threads)

if(MSVC)
    target_compile_options(fcd_core PRIVATE /W4 /utf-8 /permissive- /Zc:__cplusplus /EHsc)
    # Static CRT so the DLL does not depend on a separately installed VC++ runtime.
    set_property(TARGET fcd_core PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    target_compile_options(fcd_core PRIVATE -Wall -Wextra)
endif()

if(FCD_BUILD_TESTS)
    enable_testing()
    # Stand-ins for fastchess and a UCI engine, run by the process tests (native and C#).
    add_executable(fake_fastchess tests/fake_fastchess.cpp)
    add_executable(fake_uci_engine tests/fake_uci_engine.cpp)
    find_package(Threads REQUIRED)
    target_link_libraries(fake_fastchess PRIVATE Threads::Threads)

    add_executable(fcd_tests tests/fcd_tests.cpp tests/tools_tests.cpp tests/process_tests.cpp)
    target_link_libraries(fcd_tests PRIVATE fcd_core Threads::Threads)
    add_dependencies(fcd_tests fake_fastchess fake_uci_engine)
    target_compile_definitions(fcd_tests PRIVATE
        FCD_TEST_DATA_DIR="${CMAKE_CURRENT_SOURCE_DIR}/tests/data"
        FCD_TEST_OPENINGS_TSV="${CMAKE_CURRENT_SOURCE_DIR}/../assets/lichess-openings/lichess-openings.tsv"
        FCD_TEST_UCERL_CSV="${CMAKE_CURRENT_SOURCE_DIR}/../assets/ucerl/ucerl-ratings.csv"
        FCD_FAKE_FASTCHESS="$<TARGET_FILE:fake_fastchess>"
        FCD_FAKE_UCI="$<TARGET_FILE:fake_uci_engine>")
    if(MSVC)
        foreach(test_target fcd_tests fake_fastchess fake_uci_engine)
            target_compile_options(${test_target} PRIVATE /utf-8 /EHsc)
            set_property(TARGET ${test_target} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
        endforeach()
    endif()
    add_test(NAME fcd_tests COMMAND fcd_tests)
endif()

install(TARGETS fcd_core RUNTIME DESTINATION . LIBRARY DESTINATION .)
