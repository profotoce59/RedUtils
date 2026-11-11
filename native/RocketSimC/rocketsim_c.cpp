#include "rocketsim_c.h"

#include "RocketSim.h"

#include <cstdio>
#include <exception>

using namespace RocketSim;

// Aucune exception ne doit traverser la frontière P/Invoke : le CLR ne sait pas dérouler une
// exception C++ et le processus mourrait sans message exploitable. Chaque point d'entrée est
// donc enveloppé.
#define RSC_GUARD_BEGIN try {
#define RSC_GUARD_END(retval) } catch (const std::exception& e) { \
		std::fprintf(stderr, "[rocketsim_c] %s\n", e.what()); return retval; \
	} catch (...) { \
		std::fprintf(stderr, "[rocketsim_c] exception inconnue\n"); return retval; \
	}

static inline Vec ToVec(const RSVec3& v) { return Vec(v.x, v.y, v.z); }
static inline RSVec3 FromVec(const Vec& v) { RSVec3 r; r.x = v.x; r.y = v.y; r.z = v.z; return r; }

// ---------------------------------------------------------------- init

RSC_API int32_t rsc_init(const char* collisionMeshesFolder) {
	RSC_GUARD_BEGIN
		if (RocketSim::GetStage() == RocketSimStage::INITIALIZED)
			return 0;
		RocketSim::Init(std::filesystem::path(collisionMeshesFolder), true);
		return RocketSim::GetStage() == RocketSimStage::INITIALIZED ? 0 : -1;
	RSC_GUARD_END(-1)
}

// Fabrique en mémoire un fichier de collision minimal mais VALIDE, au format attendu par
// CollisionMeshFile::ReadFromStream (CollisionMeshFile.cpp:18-55) :
//     int32 numTris, int32 numVertices, puis numTris × (3 × int32), puis numVertices × (3 × float)
// Contrôles de validité côté RocketSim : min(numTris, numVertices) > 0 et indices dans les bornes.
// Soit, au minimum, 1 triangle et 3 sommets — 56 octets.
//
// Le triangle est placé à z = 43.5 (unités Bullet, soit ~2175 uu) : au-dessus du plafond
// (ARENA_HEIGHT = 2048 uu, matérialisé par un plan), donc rien ne peut l'atteindre, mais sous
// ArenaConfig::maxPos.z (2200 uu) pour rester dans les bornes de la broadphase.
static RocketSim::FileData MakeStubMesh() {
	RocketSim::FileData d;
	auto push32 = [&](int32_t v) {
		const byte* p = (const byte*)&v;
		d.insert(d.end(), p, p + 4);
	};
	auto pushF = [&](float v) {
		const byte* p = (const byte*)&v;
		d.insert(d.end(), p, p + 4);
	};

	push32(1);            // numTris
	push32(3);            // numVertices
	push32(0); push32(1); push32(2);   // le triangle

	const float z = 43.5f;
	pushF(0.f); pushF(0.f); pushF(z);   // sommet 0
	pushF(1.f); pushF(0.f); pushF(z);   // sommet 1
	pushF(0.f); pushF(1.f); pushF(z);   // sommet 2
	return d;
}

RSC_API int32_t rsc_init_planes_only(void) {
	RSC_GUARD_BEGIN
		if (RocketSim::GetStage() == RocketSimStage::INITIALIZED)
			return 0;

		std::map<GameMode, std::vector<RocketSim::FileData>> meshes;
		meshes[GameMode::SOCCAR] = { MakeStubMesh() };

		RocketSim::InitFromMem(meshes, true);
		return RocketSim::GetStage() == RocketSimStage::INITIALIZED ? 0 : -1;
	RSC_GUARD_END(-1)
}

RSC_API int32_t rsc_is_initialized(void) {
	return RocketSim::GetStage() == RocketSimStage::INITIALIZED ? 1 : 0;
}

// ---------------------------------------------------------------- arène

RSC_API void* rsc_arena_create(float tickRate) {
	RSC_GUARD_BEGIN
		return Arena::Create(GameMode::SOCCAR, ArenaConfig(), tickRate);
	RSC_GUARD_END(nullptr)
}

RSC_API void* rsc_arena_clone(void* arena) {
	RSC_GUARD_BEGIN
		if (!arena) return nullptr;
		// copyCallbacks = false : la copie sert à explorer un futur, pas à rejouer des événements.
		return ((Arena*)arena)->Clone(false);
	RSC_GUARD_END(nullptr)
}

RSC_API void rsc_arena_destroy(void* arena) {
	RSC_GUARD_BEGIN
		delete (Arena*)arena;
	RSC_GUARD_END()
}

RSC_API void rsc_arena_step(void* arena, int32_t ticks) {
	RSC_GUARD_BEGIN
		if (arena && ticks > 0)
			((Arena*)arena)->Step(ticks);
	RSC_GUARD_END()
}

RSC_API uint64_t rsc_arena_tick_count(void* arena) {
	return arena ? ((Arena*)arena)->tickCount : 0;
}

// ---------------------------------------------------------------- voitures

RSC_API uint32_t rsc_arena_add_car(void* arena, int32_t team) {
	RSC_GUARD_BEGIN
		if (!arena) return 0;
		Car* car = ((Arena*)arena)->AddCar(team == 0 ? Team::BLUE : Team::ORANGE);
		return car ? car->id : 0;
	RSC_GUARD_END(0)
}

RSC_API int32_t rsc_car_set_state(void* arena, uint32_t carId, const RSCarState* s) {
	RSC_GUARD_BEGIN
		if (!arena || !s) return -1;
		Car* car = ((Arena*)arena)->GetCar(carId);
		if (!car) return -1;

		CarState st = car->GetState();

		st.pos    = ToVec(s->pos);
		st.vel    = ToVec(s->vel);
		st.angVel = ToVec(s->angVel);
		st.rotMat = RotMat(ToVec(s->forward), ToVec(s->right), ToVec(s->up));

		st.isOnGround       = s->isOnGround       != 0;
		st.hasJumped        = s->hasJumped        != 0;
		st.hasDoubleJumped  = s->hasDoubleJumped  != 0;
		st.hasFlipped       = s->hasFlipped       != 0;
		st.isJumping        = s->isJumping        != 0;
		st.isFlipping       = s->isFlipping       != 0;
		st.jumpTime         = s->jumpTime;
		st.flipTime         = s->flipTime;
		st.airTimeSinceJump = s->airTimeSinceJump;
		st.flipRelTorque    = ToVec(s->flipRelTorque);

		st.boost            = s->boost;
		st.isBoosting       = s->isBoosting != 0;
		st.boostingTime     = s->boostingTime;
		st.timeSinceBoosted = s->timeSinceBoosted;
		st.supersonicTime   = s->supersonicTime;

		st.handbrakeVal     = s->handbrakeVal;
		st.isDemoed         = s->isDemoed != 0;
		st.demoRespawnTimer = s->demoRespawnTimer;

		car->SetState(st);
		return 0;
	RSC_GUARD_END(-1)
}

RSC_API int32_t rsc_car_get_state(void* arena, uint32_t carId, RSCarState* out) {
	RSC_GUARD_BEGIN
		if (!arena || !out) return -1;
		Car* car = ((Arena*)arena)->GetCar(carId);
		if (!car) return -1;

		CarState st = car->GetState();

		out->pos     = FromVec(st.pos);
		out->vel     = FromVec(st.vel);
		out->angVel  = FromVec(st.angVel);
		out->forward = FromVec(st.rotMat.forward);
		out->right   = FromVec(st.rotMat.right);
		out->up      = FromVec(st.rotMat.up);

		out->isOnGround       = st.isOnGround;
		out->hasJumped        = st.hasJumped;
		out->hasDoubleJumped  = st.hasDoubleJumped;
		out->hasFlipped       = st.hasFlipped;
		out->isJumping        = st.isJumping;
		out->isFlipping       = st.isFlipping;
		out->jumpTime         = st.jumpTime;
		out->flipTime         = st.flipTime;
		out->airTimeSinceJump = st.airTimeSinceJump;
		out->flipRelTorque    = FromVec(st.flipRelTorque);

		out->boost            = st.boost;
		out->isBoosting       = st.isBoosting;
		out->boostingTime     = st.boostingTime;
		out->timeSinceBoosted = st.timeSinceBoosted;
		out->supersonicTime   = st.supersonicTime;

		out->handbrakeVal     = st.handbrakeVal;
		out->isDemoed         = st.isDemoed;
		out->demoRespawnTimer = st.demoRespawnTimer;

		out->ballHitValid = st.ballHitInfo.isValid;
		out->ballHitTick  = st.ballHitInfo.tickCountWhenHit;
		return 0;
	RSC_GUARD_END(-1)
}

RSC_API int32_t rsc_car_set_controls(void* arena, uint32_t carId, const RSCarControls* c) {
	RSC_GUARD_BEGIN
		if (!arena || !c) return -1;
		Car* car = ((Arena*)arena)->GetCar(carId);
		if (!car) return -1;

		CarControls ctrl;
		ctrl.throttle  = c->throttle;
		ctrl.steer     = c->steer;
		ctrl.pitch     = c->pitch;
		ctrl.yaw       = c->yaw;
		ctrl.roll      = c->roll;
		ctrl.jump      = c->jump      != 0;
		ctrl.boost     = c->boost     != 0;
		ctrl.handbrake = c->handbrake != 0;
		ctrl.ClampFix();

		car->controls = ctrl;
		return 0;
	RSC_GUARD_END(-1)
}

// ---------------------------------------------------------------- balle

RSC_API void rsc_ball_set_state(void* arena, const RSBallState* s) {
	RSC_GUARD_BEGIN
		if (!arena || !s) return;
		Ball* ball = ((Arena*)arena)->ball;
		BallState st = ball->GetState();
		st.pos    = ToVec(s->pos);
		st.vel    = ToVec(s->vel);
		st.angVel = ToVec(s->angVel);
		ball->SetState(st);
	RSC_GUARD_END()
}

RSC_API void rsc_ball_get_state(void* arena, RSBallState* out) {
	RSC_GUARD_BEGIN
		if (!arena || !out) return;
		BallState st = ((Arena*)arena)->ball->GetState();
		out->pos    = FromVec(st.pos);
		out->vel    = FromVec(st.vel);
		out->angVel = FromVec(st.angVel);
	RSC_GUARD_END()
}
