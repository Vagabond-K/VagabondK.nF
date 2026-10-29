cmake_minimum_required(VERSION 3.16)
project(VagabondKMmsPeer C)
if(NOT LIBIEC61850_SOURCE)
    message(FATAL_ERROR "LIBIEC61850_SOURCE is required")
endif()
set(BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(CONFIG_INCLUDE_GOOSE_SUPPORT OFF CACHE BOOL "" FORCE)
set(CONFIG_IEC61850_L2_GOOSE OFF CACHE BOOL "" FORCE)
set(CONFIG_IEC61850_L2_SMV OFF CACHE BOOL "" FORCE)
set(CONFIG_IEC61850_R_GOOSE OFF CACHE BOOL "" FORCE)
set(CONFIG_IEC61850_R_SMV OFF CACHE BOOL "" FORCE)
add_subdirectory("${LIBIEC61850_SOURCE}" libiec61850)
add_executable(VagabondKMmsPeer peer.c)
target_include_directories(VagabondKMmsPeer PRIVATE
    "${LIBIEC61850_SOURCE}/src/iec61850/inc"
    "${LIBIEC61850_SOURCE}/src/mms/inc"
    "${LIBIEC61850_SOURCE}/src/common/inc"
    "${LIBIEC61850_SOURCE}/src/logging"
    "${LIBIEC61850_SOURCE}/hal/inc"
    "${LIBIEC61850_SOURCE}/src/hal/inc"
    "${CMAKE_CURRENT_BINARY_DIR}/libiec61850/config")
target_link_libraries(VagabondKMmsPeer PRIVATE iec61850)
