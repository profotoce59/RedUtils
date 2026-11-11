using System;
using System.Timers;
using System.Collections.Generic;
using RedUtils.Math;
using RLBotDotNet;
using rlbot.flat;
using Color = System.Drawing.Color;

namespace RedUtils
{
	/// <summary>The main class in RedUtils. 
	/// <para>It contains properties that are unique to your bot, such as your bot's car, your teammates, etc.</para>
	/// <para>It also receives the GameTickPacket from the RLBot framework, and processes all the data in it.</para>
	/// </summary>
	public abstract partial class RUBot : Bot
	{
		/// <summary>A tool to draw debug lines in-game</summary>
		public new ExtendedRenderer Renderer { get; internal set; }

		/// <summary>Your car</summary>
		public Car Me => Index < Cars.Count ? Cars.AllCars[Index] : new Car();

		/// <summary>A list of all of the cars on your team (not including yourself, and including ones that are respawning)</summary>
		public List<Car> Teammates 
		{ 
			get 
			{ 
				List<Car> teammates = Cars.AllCars.FindAll(car => car.Team == Team);
				teammates.Remove(Me);
				return teammates;
			} 
		}
		/// <summary>A list of all of the cars on your team (not including yourself, and NOT including ones that are respawning)</summary>
		public List<Car> LivingTeammates
		{
			get
			{
				List<Car> teammates = Cars.AllCars.FindAll(car => !car.IsDemolished && car.Team == Team);
				teammates.Remove(Me);
				return teammates;
			}
		}

		/// <summary>A list of all of the cars on the opposing team (including ones that are respawning)</summary>
		public List<Car> Opponents => Cars.AllCars.FindAll(car => car.Team != Team);
		/// <summary>A list of all of the cars on the opposing team (NOT including ones that are respawning)</summary>
		public List<Car> LivingOpponents => Cars.AllCars.FindAll(car => !car.IsDemolished && car.Team != Team);

		/// <summary>Our team's goal</summary>
		public Goal OurGoal => Field.Goals[Team];
		/// <summary>The opposing team's goal</summary>
		public Goal TheirGoal => Field.Goals[1 - Team];

		/// <summary>The current score for your team</summary>
		public int OurScore => Game.Scores[Team];
		/// <summary>The current score for the opposing teams</summary>
		public int TheirScore => Game.Scores[1 - Team];

		/// <summary>Whether or not it's time for kickoff</summary>
		public bool IsKickoff { get; private set; }
		/// <summary>The change in time since the last tick</summary>
		public float DeltaTime { get; private set; }

		/// <summary>The inputs that are returned to RLBot every tick</summary>
		public Controller Controller = new Controller();
		/// <summary>
		/// The action that will be executed every tick, along side the "Run" function. 
		/// <para>Resets when the ball is touched, unless the action isn't interruptible. Otherwise only resets when the action has finished</para>
		/// </summary>
		public IAction Action = null;

		/// <summary>Whether the "GetReady" function has run</summary>
		private bool _ready = false;
		/// <summary>The last time the ball was touched</summary>
		private float _lastTouchTime;
		/// <summary>The previous moment in time. <para>Used to calculate DeltaTime</summary>
		private float _lastTime = 0;

		/// <summary>Me/Ball locations from the previous tick, used to detect a state-set (teleport)</summary>
		private Vec3 _lastMeLocation = Vec3.Zero;
		private Vec3 _lastBallLocation = Vec3.Zero;
		private bool _positionsPrimed = false;
		/// <summary>
		/// A single-tick displacement below this is always physically explainable, so it never
		/// triggers a false-positive teleport detection even at very small DeltaTime.
		/// </summary>
		private const float TeleportMargin = 400f;

		//a
		public RUBot(string botName, int botTeam, int botIndex) : base(botName, botTeam, botIndex)
		{
			Console.WriteLine($"RedUtils bot \"{botName}\" is up and running.");
		}

		/// <summary>Initializes some static classes using data from the packet</summary>
		/// <param name="packet">Contains all information about the current game state</param>
		private void GetReady(GameTickPacket packet)
		{
			Renderer = new ExtendedRenderer(base.Renderer);
			Field.Initialize(GetFieldInfo());
			Cars.Initialize(packet);
			_ready = true;
		}

		/// <summary>Processes data from the packet, and uses said data to update some static classes</summary>
		/// <param name="packet">Contains all information about the current game state</param>
		private void Process(GameTickPacket packet)
		{
			// A reliable teleport check needs last tick's Me/Ball to still refer to the same car
			// index and to a game that was already running - skip it across (re)initialization.
			bool canCheckTeleport = _ready && _positionsPrimed && Cars.Count == packet.PlayersLength && Index < Cars.Count;
			Vec3 prevMeLocation = canCheckTeleport ? Me.Location : Vec3.Zero;
			Vec3 prevBallLocation = canCheckTeleport ? Ball.Location : Vec3.Zero;

			if (Cars.Count != packet.PlayersLength)
			{
				// Reinitializes the cars if someone has left or joined the game
				Cars.Initialize(packet);
			}
			else
			{
				// Updates the cars' positions, velocities, etc
				Cars.Update(packet);
			}
			// Updates the ball's position, velocity, etc
			Ball.Update(this, packet.Ball.Value);
			// Updates the game's score, time, etc
			Game.Update(packet);
			// Updates the boost pads
			Field.Update(packet);

			if (canCheckTeleport)
			{
				// A single-tick jump far beyond what physics allows at the observed DeltaTime means
				// something external (state-setting, a test script) moved the ball or our car. The
				// action we were mid-way through, and any latched decision state built around the
				// old positions, is now stale - reset immediately instead of chasing a scenario that
				// no longer exists.
				float meLimit = MathF.Max(TeleportMargin, Car.MaxSpeed * DeltaTime * 5f);
				float ballLimit = MathF.Max(TeleportMargin * 1.5f, Ball.MaxSpeed * DeltaTime * 5f);
				if (prevMeLocation.Dist(Me.Location) > meLimit || prevBallLocation.Dist(Ball.Location) > ballLimit)
				{
					Action = null;
					OnStateSet();
				}
			}
			_positionsPrimed = true;

			if (!IsKickoff && Game.IsKickoffPause && Game.IsRoundActive)
			{
				// Reset the action right as a kickoff starts
				Action = null;
			}

			IsKickoff = Game.IsKickoffPause && Game.IsRoundActive;
		}

		/// <summary>
		/// Called the instant a teleport (state-setting) is detected on the ball or your car.
		/// <para>Action is already reset by the time this runs. Override this to also clear any
		/// latched decision state your strategy keeps between ticks (role hysteresis, state
		/// stabilization timers, sticky flags...), so your bot recomputes everything fresh on the
		/// very next tick instead of continuing to reason from before the reset.</para>
		/// </summary>
		protected virtual void OnStateSet() { }

		/// <summary>
		/// Runs after the current action has written its inputs, just before they're returned to
		/// RLBot. Override to correct or override those inputs.
		///
		/// <para>Strategy code in <see cref="Run"/> runs BEFORE the action, so anything it writes to
		/// <c>Controller</c> is overwritten by <c>Action.Run</c>. This hook is the only place where a
		/// correction computed outside the action — a simulated strike plan, a safety clamp — can
		/// actually reach the game.</para>
		/// </summary>
		protected virtual void AfterAction() { }

		/// <summary>Updates DeltaTime... pretty self explanitory</summary>
		private void UpdateDeltaTime()
		{
			DeltaTime = Game.Time - _lastTime;
			_lastTime = Game.Time;
		}

		/// <summary>The function that gets called every tick to get inputs from your bot</summary>
		/// <param name="packet">Contains all information about the current game state</param>
		/// <returns>The bot's inputs for that tick</returns>
		public override Controller GetOutput(GameTickPacket packet)
		{
			// Resets the Controller every tick
			Controller = new Controller(); 

			if (!_ready)
			{
				// Gets the packet ready for the first time during startup
				GetReady(packet); 
			}
			// Proccesses the packet so that data is up to date during this frame
			Process(packet);

			// Runs our strategy code
			Run(); 

			// if there is an action to execute...
			if (Action != null)
			{
				Action.Run(this); // execute it!

				// If the ball hasn't been touched, set it to -1, so we don't get errors.
				float latestTouchTime = Ball.LatestTouch == null ? -1 : Ball.LatestTouch.Time; 
				if (Action.Finished || (_lastTouchTime != latestTouchTime && Action.Interruptible) || Me.IsDemolished) 
				{
					// If the action has completed, or the ball has been touched and the action is interruptible,
					// or if our bot is demolished reset the action
					_lastTouchTime = latestTouchTime;
					Action = null;
				}
			}

			// Last word on the inputs, after the action has had its say.
			AfterAction();

			UpdateDeltaTime();

			// returns our inputs to RLBot
			return Controller;
		}

		/// <summary>Runs every tick. Overwrite with your own strategy code!</summary>
		public abstract void Run();

		/// <summary>Gets the ball prediction struct from the framework</summary>
		internal new BallPrediction GetBallPrediction() => new BallPrediction(base.GetBallPrediction());
	}
}
