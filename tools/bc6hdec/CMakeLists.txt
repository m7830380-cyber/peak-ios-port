cmake_minimum_required(VERSION 3.15)
project(bc6hdec C)
# bcdec.h (Unlicense / MIT) is fetched at configure time, pinned by hash.
set(BCDEC_H ${CMAKE_CURRENT_BINARY_DIR}/bcdec.h)
if(NOT EXISTS ${BCDEC_H})
  file(DOWNLOAD https://raw.githubusercontent.com/iOrange/bcdec/main/bcdec.h ${BCDEC_H}
       EXPECTED_HASH SHA256=134520764d96f70a27db814173616c89ad85db95fbd3328417c7a8c77e7ca189)
endif()
add_library(bc6hdec SHARED bc6hdec.c)
target_include_directories(bc6hdec PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
