using ProjectIGI.Core.Numerics;
using ProjectIGI.Game.Igi2.Soldiers;
using ProjectIGI.Game.Igi2.Weapons;

namespace ProjectIGI.Game.Igi2.Ai.Runtime;

/// <content>
/// What a mind with nothing to run does instead of standing frozen: a soldier whose script or graph is missing
/// hears nothing on the normal path and is never stepped past the tick's early return, so it stands mindless where
/// it spawned. The fallback is deliberately small — it runs only on that dead path, never for a soldier with a
/// script and a graph — and it never fights: it walks to a noise it heard, looks at it, and otherwise holds its
/// post and looks around. A cutscene leaves it exactly as the tick left it before.
/// </content>
public sealed partial class Igi2HumanAi
{
    /// <summary>What the fallback is doing.</summary>
    private enum FallbackState
    {
        /// <summary>Holding its post, waiting to look aside.</summary>
        Idle,

        /// <summary>Turning to look aside.</summary>
        LookAside,

        /// <summary>Watching aside.</summary>
        HoldAside,

        /// <summary>Turning back to where it faced.</summary>
        LookBack,

        /// <summary>Walking to a noise it heard.</summary>
        Investigate,

        /// <summary>Watching the place a noise came from.</summary>
        InvestigateLook,
    }

    /// <summary>Ticks of walking toward a noise before giving up: ten seconds.</summary>
    private const int InvestigateBudget = 300;

    /// <summary>Ticks of watching the place a noise came from once there: two seconds.</summary>
    private const int InvestigateLookTicks = 60;

    /// <summary>Ticks a turn to look aside or back may take before it is given up: four seconds.</summary>
    private const int LookTurnBudget = 120;

    /// <summary>The turn that counts as facing, five degrees, as <see cref="FaceTarget"/> uses.</summary>
    private const float FacingTolerance = 0.0872664675116539f;

    /// <summary>How far ahead a point to look at is put: four metres.</summary>
    private const float LookDistance = 16384f;

    private FallbackState _fallbackState;
    private int _fallbackTimer;
    private int _fallbackBudget;
    private int _fallbackNode = -1;
    private bool _fallbackPrimed;
    private Vector3d _fallbackTarget;
    private Vector3d _fallbackReturn;

    /// <summary>
    /// Whether an event kind is a sound worth investigating: steps, landings, doors, fences, ladders, gunfire and
    /// weapon handling. Cries, explosions and the alarm stay with the squad and the script, which the fallback never
    /// speaks for.
    /// </summary>
    /// <param name="kind">The event kind.</param>
    /// <returns><see langword="true"/> when it is a sound to walk to.</returns>
    private static bool IsFallbackSound(int kind) => kind is Igi2AiEvent.Walk or Igi2AiEvent.GroundImpact
        or Igi2AiEvent.Door or Igi2AiEvent.Fence or Igi2AiEvent.Ladder or Igi2GunFire.ShotEvent
        or Igi2AiEvent.GunShotMiss or Igi2GunFire.ShotLineEvent or Igi2AiEvent.HandleWeapon;

    /// <summary>
    /// Weighs an event for the fallback: a mind the tick leaves standing hears a sound the same gates allow —
    /// within reach, its flags passed — and remembers where it came from, to walk to. A scripted soldier with a
    /// graph never reaches here with a sound it should act on: its sounds stay dropped as today, and its script
    /// keeps its orders exactly.
    /// </summary>
    /// <param name="aiEvent">The event.</param>
    private void ReceiveFallback(in Igi2AiEvent aiEvent)
    {
        if (Script is not null && Graph is not null)
        {
            return;
        }

        if (aiEvent.Position is not { } at || !IsFallbackSound(aiEvent.Event))
        {
            return;
        }

        // Flagless events carry no reach: only a sound that passes the same gates as a heard event is
        // investigated, so the fallback hears no further than any soldier.
        if (aiEvent.Flags == 0 || !PassesFlags(aiEvent))
        {
            return;
        }

        _fallbackTarget = at;
        _fallbackTimer = 0;
        _fallbackBudget = InvestigateBudget;
        _fallbackNode = -1;
        if (Graph is { } graph && CurrentNode >= 0 && graph.Nearest(at, allowVehicleNodes: false) is { } node)
        {
            // The way is probed before it is walked: a node no path reaches kills the soldier in MoveToNode, as
            // the original does, and the fallback must never do that. With no path the soldier steers straight
            // at the noise instead, and gives up on its budget.
            PathSearch.Clear();
            if (PathSearch.CreatePath(graph, CurrentNode, node.Id, uint.MaxValue))
            {
                _fallbackNode = node.Id;
            }

            PathSearch.Clear();
        }

        _fallbackState = FallbackState.Investigate;
    }

    /// <summary>
    /// Runs the fallback one tick, for a mind with no script or no graph: the noise it heard is walked to, otherwise
    /// it holds its post and looks around, and its body is stepped as the normal path steps it. A cutscene forgets
    /// any noise and leaves the body as it stands, as the tick left it before; a soldier riding something, or
    /// manning a gun, is never moved.
    /// </summary>
    /// <param name="soldier">The body.</param>
    /// <param name="detect">Whether the mind runs its spotting half; false while a cutscene plays.</param>
    private void RunFallback(Igi2HumanSoldier soldier, bool detect)
    {
        if (!detect || soldier.AttachParent is not null || GunnerId != -1)
        {
            ClearFallback();
            return;
        }

        if (!soldier.IsDead)
        {
            TickFallback();
        }

        if (soldier.IsActive)
        {
            soldier.Update(_channels);
            Array.Clear(_channels);
        }
    }

    /// <summary>Forgets the fallback's state: idle again, with a fresh wait before it first looks aside.</summary>
    private void ClearFallback()
    {
        _fallbackState = FallbackState.Idle;
        _fallbackTimer = 0;
        _fallbackBudget = 0;
        _fallbackNode = -1;
        _fallbackPrimed = false;
    }

    /// <summary>Goes back to holding its post, with a fresh drawn wait before it looks aside again.</summary>
    private void RestIdle()
    {
        _fallbackState = FallbackState.Idle;
        _fallbackTimer = 150 + (int)(World.Random.NextSingle() * 270f);
    }

    /// <summary>One tick of the fallback's state.</summary>
    private void TickFallback()
    {
        switch (_fallbackState)
        {
            case FallbackState.Idle:
                if (!_fallbackPrimed)
                {
                    _fallbackPrimed = true;
                    _fallbackTimer = 60 + (int)(World.Random.NextSingle() * 300f);
                }

                if (_fallbackTimer > 0)
                {
                    _fallbackTimer--;
                    break;
                }

                StartLookAside();
                break;
            case FallbackState.LookAside:
                SelectTurnSpeed();
                if (FacePosition(_fallbackTarget, FacingTolerance, level: true) || --_fallbackTimer <= 0)
                {
                    _fallbackState = FallbackState.HoldAside;
                    _fallbackTimer = 30 + (int)(World.Random.NextSingle() * 30f);
                }

                break;
            case FallbackState.HoldAside:
                if (--_fallbackTimer <= 0)
                {
                    _fallbackState = FallbackState.LookBack;
                    _fallbackTimer = LookTurnBudget;
                }

                break;
            case FallbackState.LookBack:
                SelectTurnSpeed();
                if (FacePosition(_fallbackReturn, FacingTolerance, level: true) || --_fallbackTimer <= 0)
                {
                    RestIdle();
                }

                break;
            case FallbackState.Investigate:
                if (TickInvestigate())
                {
                    ClearMovementQueue();
                    _fallbackState = FallbackState.InvestigateLook;
                    _fallbackTimer = InvestigateLookTicks;
                }

                break;
            case FallbackState.InvestigateLook:
                SelectTurnSpeed();
                FacePosition(_fallbackTarget, FacingTolerance, level: true);
                if (--_fallbackTimer <= 0)
                {
                    RestIdle();
                }

                break;
        }
    }

    /// <summary>
    /// Starts a look aside: a point off to one side, at the look distance, to turn to — and the point straight
    /// ahead of where it looks now, to turn back to.
    /// </summary>
    private void StartLookAside()
    {
        Vector3d at = Soldier.Position;
        float[] o = Soldier.OrientationRows;
        _fallbackReturn = new Vector3d(
            (o[1] * LookDistance) + at.X, (o[4] * LookDistance) + at.Y, (o[7] * LookDistance) + at.Z);
        float angle = (0.6f + (World.Random.NextSingle() * 0.6f)) * (World.Random.NextSingle() < 0.5f ? 1f : -1f);
        float s = (float)(Math.Sin(angle) * LookDistance);
        float c = (float)(Math.Cos(angle) * LookDistance);
        _fallbackTarget = new Vector3d(
            (c * o[1]) + (s * o[0]) + at.X, (c * o[4]) + (s * o[3]) + at.Y, (c * o[7]) + (s * o[6]) + at.Z);
        _fallbackState = FallbackState.LookAside;
        _fallbackTimer = LookTurnBudget;
    }

    /// <summary>
    /// Walks one tick toward the noise: along the graph where a path was probed, else straight at it, at a walk.
    /// </summary>
    /// <returns><see langword="true"/> once there, or once the walk is given up.</returns>
    private bool TickInvestigate()
    {
        if (IsDestinationReached(_fallbackTarget))
        {
            return true;
        }

        if (--_fallbackBudget <= 0)
        {
            return true;
        }

        MakeWalk();
        if (_fallbackNode >= 0)
        {
            if (PatrolMoveToNode(_fallbackNode))
            {
                return true;
            }
        }
        else
        {
            SelectTurnSpeed();
            PointTowards(_fallbackTarget);
        }

        return false;
    }
}
