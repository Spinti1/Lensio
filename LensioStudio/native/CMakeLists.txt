cmake_minimum_required(VERSION 3.20)
project(LensioVirtualCamera LANGUAGES C CXX)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(CMAKE_CXX_STANDARD 17)
set(V plugins/win-dshow/virtualcam-module)
add_library(lensio-virtualcam MODULE
 ${V}/virtualcam-module.cpp ${V}/virtualcam-filter.cpp ${V}/placeholder.cpp ${V}/sleepto.c
 shared/obs-shared-memory-queue/shared-memory-queue.c shared/obs-tiny-nv12-scale/tiny-nv12-scale.c
 deps/libdshowcapture/source/output-filter.cpp deps/libdshowcapture/source/dshow-base.cpp
 deps/libdshowcapture/source/dshow-enum.cpp deps/libdshowcapture/source/dshow-formats.cpp
 deps/libdshowcapture/source/dshow-media-type.cpp deps/libdshowcapture/source/log.cpp)
target_include_directories(lensio-virtualcam PRIVATE ${V} libobs shared/obs-shared-memory-queue shared/obs-tiny-nv12-scale deps/libdshowcapture)
target_compile_definitions(lensio-virtualcam PRIVATE VIRTUALCAM_AVAILABLE UNICODE _UNICODE _CRT_SECURE_NO_WARNINGS)
target_compile_options(lensio-virtualcam PRIVATE /O2 /Gy /utf-8)
target_link_options(lensio-virtualcam PRIVATE /OPT:REF /OPT:ICF /DYNAMICBASE /NXCOMPAT /Brepro /INCREMENTAL:NO)
target_link_libraries(lensio-virtualcam PRIVATE gdiplus strmiids winmm ole32 oleaut32 advapi32 shlwapi shell32 setupapi cfgmgr32)
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
 set(BITS 64)
else()
 set(BITS 32)
endif()
target_sources(lensio-virtualcam PRIVATE ${V}/cmake/windows/virtualcam-module${BITS}.def)
set_target_properties(lensio-virtualcam PROPERTIES OUTPUT_NAME "lensio-virtualcam-module${BITS}")
