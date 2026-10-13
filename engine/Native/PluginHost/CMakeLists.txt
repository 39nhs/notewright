cmake_minimum_required(VERSION 3.25)
project(GrazePluginHost VERSION 1.0.0 LANGUAGES CXX)
if(NOT WIN32 OR NOT VST3_SDK_ROOT)
  message(FATAL_ERROR "Build for Windows x64 and set VST3_SDK_ROOT to the pinned VST3 SDK.")
endif()
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
add_subdirectory(${VST3_SDK_ROOT} sdk)
add_executable(graze-plugin-host host.cpp
  ${VST3_SDK_ROOT}/public.sdk/source/common/memorystream.cpp
  ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/module_win32.cpp
  ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/plugprovider.cpp)
target_compile_features(graze-plugin-host PRIVATE cxx_std_17)
target_link_libraries(graze-plugin-host PRIVATE sdk_hosting ole32)
if(MINGW)
  target_link_options(graze-plugin-host PRIVATE -static)
endif()
