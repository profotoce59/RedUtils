cmake_minimum_required(VERSION 3.16)
project("RocketSimC" LANGUAGES CXX)

# Chemin vers le clone de RocketSim. Par défaut : ../../../refs/RocketSim depuis ce fichier,
# soit HardcodeBot/refs/RocketSim. Surchargeable : -DROCKETSIM_DIR=...
set(ROCKETSIM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../refs/RocketSim" CACHE PATH
    "Racine du dépôt RocketSim")

if(NOT EXISTS "${ROCKETSIM_DIR}/src/RocketSim.h")
    message(FATAL_ERROR "RocketSim introuvable dans ${ROCKETSIM_DIR}. "
                        "Passer -DROCKETSIM_DIR=<chemin du clone>.")
endif()

# RocketSim est construit en bibliothèque STATIQUE et englobé dans notre DLL : on ne distribue
# ainsi qu'un seul binaire natif à côté de Bot.exe, au lieu de deux.
add_subdirectory("${ROCKETSIM_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/rocketsim")

add_library(RocketSimC SHARED rocketsim_c.cpp rocketsim_c.h)

set_target_properties(RocketSimC PROPERTIES
    CXX_STANDARD 20
    CXX_STANDARD_REQUIRED ON
    # Nom du fichier attendu par le DllImport côté C# (RocketSimC.dll / libRocketSimC.so)
    OUTPUT_NAME "RocketSimC"
    POSITION_INDEPENDENT_CODE ON)

target_include_directories(RocketSimC PRIVATE
    "${ROCKETSIM_DIR}/src"
    "${ROCKETSIM_DIR}/libsrc/bullet3-3.24")

target_link_libraries(RocketSimC PRIVATE RocketSim)

# RocketSim vise MSVC. Sous GCC/Clang, MathTypes.h contient un reinterpret_cast dans une
# fonction constexpr, que ces compilateurs refusent alors que MSVC l'accepte. Ne concerne pas
# la façade elle-même, qui compile proprement.
if(NOT MSVC)
    message(WARNING "Compilation hors MSVC : RocketSim peut nécessiter un correctif dans "
                    "Math/MathTypes/MathTypes.h (operator[] constexpr).")
endif()
