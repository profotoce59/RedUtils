// Façade C plate au-dessus de RocketSim (C++), destinée au P/Invoke depuis C#.
//
// POURQUOI UNE FAÇADE ET PAS UN MARSHALLING DIRECT DE CarState :
//   RocketSim::Vec est déclaré RS_ALIGN_16 avec un 4e float caché (`_w`) pour forcer le
//   compilateur à utiliser le SIMD. sizeof(Vec) vaut donc 16 et non 12. Une structure C#
//   qui recopierait naïvement CarState avec des Vector3 de 3 floats serait décalée dès le
//   premier champ, silencieusement. RotMat a le même problème (3 Vec alignés = 48 octets).
//   On expose donc des structures plates dont on contrôle les deux côtés, en float/int32
//   uniquement — pas de bool (dont la taille de marshalling varie), pas d'alignement exotique.
//
// La rotation est exposée en vecteurs de base (forward/right/up) et non en angles d'Euler :
//   RocketSim stocke déjà une RotMat sous cette forme, et RedUtils expose Car.Forward/Right/Up.
//   Passer par des Euler ajouterait une convention à faire correspondre, donc un bug possible.
//
// Les champs jumpTime/flipTime/isFlipping/airTimeSinceJump ne sont PAS fournis par le paquet
// RLBot. Ils gouvernent le déclenchement des flips — donc l'objet même d'une recherche de tir.
// Ils doivent être suivis tick par tick côté bot. Voir AUDIT §7.2.

#ifndef ROCKETSIM_C_H
#define ROCKETSIM_C_H

#include <stdint.h>

#ifdef _WIN32
	#define RSC_API __declspec(dllexport)
#else
	#define RSC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct { float x, y, z; } RSVec3;

typedef struct {
	RSVec3 pos, vel, angVel;
	RSVec3 forward, right, up;   // RotMat, en vecteurs de base

	int32_t isOnGround;
	int32_t hasJumped, hasDoubleJumped, hasFlipped;
	int32_t isJumping, isFlipping;
	float   jumpTime, flipTime, airTimeSinceJump;
	RSVec3  flipRelTorque;

	float   boost;
	int32_t isBoosting;
	float   boostingTime, timeSinceBoosted, supersonicTime;

	float   handbrakeVal;
	int32_t isDemoed;
	float   demoRespawnTimer;

	// Lecture seule : renseignés par rsc_car_get_state, ignorés par rsc_car_set_state.
	// Permettent de détecter le contact avec la balle et à quel tick il a eu lieu.
	int32_t  ballHitValid;
	uint64_t ballHitTick;
} RSCarState;

typedef struct { RSVec3 pos, vel, angVel; } RSBallState;

typedef struct {
	float   throttle, steer, pitch, yaw, roll;
	int32_t jump, boost, handbrake;
} RSCarControls;

// --- Initialisation globale (une fois par processus) ---
// collisionMeshesFolder : dossier produit par RLArenaCollisionDumper.
// Retour : 0 = ok, -1 = échec (message sur stderr).
RSC_API int32_t rsc_init(const char* collisionMeshesFolder);

// Initialise SANS les meshes de collision du jeu.
//
// Le sol, le plafond et les murs latéraux ne sont PAS des meshes : Arena::_SetupArenaCollisionShapes
// les construit en btStaticPlaneShape à partir des constantes (Arena.cpp:1029+). Les meshes ne
// couvrent que les COINS ARRONDIS, les RAMPES et la GÉOMÉTRIE DES BUTS.
//
// RocketSim refuse toutefois de démarrer si la liste de meshes est vide (Arena.cpp:997). On lui
// fournit donc, via InitFromMem, un unique triangle valide placé au-dessus du plafond — donc
// physiquement inatteignable, la balle étant bloquée par le plan de plafond à z=2048.
//
// CE QUI RESTE EXACT : la frappe voiture-balle, les rebonds sol/plafond/murs latéraux, la
// balistique. Soit tout ce dont on a besoin pour juger un tir direct.
//
// CE QUI DEVIENT FAUX, SANS AUCUN SIGNAL : les coins arrondis, le fond de terrain autour des
// buts, l'entrée dans le but. Une balle envoyée là-bas traversera le vide.
//
// Retour : 0 = ok, -1 = échec.
RSC_API int32_t rsc_init_planes_only(void);

RSC_API int32_t rsc_is_initialized(void);

// --- Arène ---
// Mode Soccar uniquement : c'est le seul dont le bot a besoin.
RSC_API void*    rsc_arena_create(float tickRate);
RSC_API void*    rsc_arena_clone(void* arena);
RSC_API void     rsc_arena_destroy(void* arena);
RSC_API void     rsc_arena_step(void* arena, int32_t ticks);
RSC_API uint64_t rsc_arena_tick_count(void* arena);

// --- Voitures ---
// Retourne l'id de la voiture créée, ou 0 en cas d'échec.
RSC_API uint32_t rsc_arena_add_car(void* arena, int32_t team);
RSC_API int32_t  rsc_car_set_state(void* arena, uint32_t carId, const RSCarState* state);
RSC_API int32_t  rsc_car_get_state(void* arena, uint32_t carId, RSCarState* out);
RSC_API int32_t  rsc_car_set_controls(void* arena, uint32_t carId, const RSCarControls* controls);

// --- Balle ---
RSC_API void rsc_ball_set_state(void* arena, const RSBallState* state);
RSC_API void rsc_ball_get_state(void* arena, RSBallState* out);

#ifdef __cplusplus
}
#endif

#endif // ROCKETSIM_C_H
