using System.Numerics;

namespace OsuStableRemake;

// stable's osu! star rating, for maps its database hasn't rated (imported by the remake). Three skills run over the
// objects in order: aim (a continuous strain whose peaks are kept per variable-length section), speed (with a rhythm
// multiplier) and reading; their difficulties become performance-like values that are p-norm summed and cube-rooted.
// Positions are the gameplay ones (stacked, HR-flipped), times are divided by the mod rate. Checked against the
// ratings osu!.db holds (--check-stars).
static class StarRating
{
    public static double Compute(Beatmap map, Mods mods)
    {
        try { return new Calculation(map, mods).Stars(); }
        catch (Exception e) when (e is DivideByZeroException or ArgumentException or IndexOutOfRangeException) { return 0; } // stable: 0 when it throws
    }

    // ---- numeric helpers (stable's own forms: small whole powers multiply out, the rest use Math.Pow)
    static double Pow(double x, int n) => n switch { 0 => 1, 1 => x, 2 => x * x, 3 => x * x * x, 4 => x * x * x * x, 5 => x * x * x * x * x, _ => Math.Pow(x, n) };
    static double Clamp01(double x) => Math.Clamp(x, 0.0, 1.0);
    static double Linear(double x, double a, double b) => Clamp01((x - a) / (b - a));
    static double Smoothstep(double x, double a, double b) { x = Clamp01((x - a) / (b - a)); return x * x * (3.0 - 2.0 * x); }
    static double Smootherstep(double x, double a, double b) { x = Clamp01((x - a) / (b - a)); return x * x * x * (x * (6.0 * x - 15.0) + 10.0); }
    static double BellCurve(double x) { x = 0.5 - Math.Abs(x - 0.5); x = Clamp01(x * 2.0); return x * x * (3.0 - 2.0 * x); }
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double Logistic(double x, double midpoint, double multiplier, double max) => max / (1.0 + Math.Exp(multiplier * (midpoint - x)));
    static double PNorm(double p, params double[] values) { double s = 0; foreach (var v in values) s += Math.Pow(v, p); return Math.Pow(s, 1.0 / p); }
    static double Rad(double degrees) => degrees * Math.PI / 180.0;
    static double MsToBpm(double ms, int divisor) => 60000.0 / (ms * divisor);
    static double BpmToMs(double bpm, int divisor) => 60000.0 / divisor / bpm;

    // ---- a slider's nested points: head, ticks (per span; reversed spans backwards), repeats, tail
    enum NestedKind { Tick, Head, Tail, Repeat }
    readonly record struct Nested(NestedKind Kind, double Time, Vector2 Pos);

    static List<Nested> NestedPoints(Beatmap map, HitObject o)
    {
        var list = new List<Nested>();
        // stable makes these before it stacks the objects, so a stacked slider's points stay where the map put them
        Vector2 head = o.Pos;
        Vector2 At(double progress) => head + (o.PointAt((float)(Clamp01(progress) * o.Length)) - o.Pos);
        int spans = o.Slides, start = (int)o.Time;
        double velocity = o.StableVelocity / 1000.0, length = o.Length;
        double spanDuration = ((double)start + (double)spans * length / velocity - (double)start) / (double)spans;
        var tp = map.PointAt(o.Time);
        double tickFactor = tp.BeatLength < 0 ? (double)Math.Clamp((float)-tp.BeatLength, 10f, 1000f) / 100.0 : 1;
        double scoringDist = 100.0 * map.SliderMultiplier / map.SliderTickRate;
        double tickDistance = map.FormatVersion < 8 ? scoringDist : scoringDist / tickFactor;

        double len = Math.Min(100000.0, length);
        tickDistance = Math.Clamp(tickDistance, 0.0, len);
        double minFromEnd = velocity * 10.0;
        list.Add(new(NestedKind.Head, start, head));
        if (tickDistance != 0)
            for (int span = 0; span < spans; span++)
            {
                double spanStart = start + span * spanDuration;
                bool reversed = span % 2 == 1;
                var ticks = new List<Nested>();
                for (double d = tickDistance; d <= len && !(d >= len - minFromEnd); d += tickDistance)
                {
                    double p = d / len;
                    ticks.Add(new(NestedKind.Tick, spanStart + (reversed ? 1.0 - p : p) * spanDuration, At(p)));
                }
                if (reversed) ticks.Reverse();
                list.AddRange(ticks);
                if (span < spans - 1) list.Add(new(NestedKind.Repeat, (double)start + (double)(span + 1) * spanDuration, At((span + 1) % 2)));
            }
        list.Add(new(NestedKind.Tail, start + spans * spanDuration, o.EndPos));
        return list;
    }

    // ---- per-object difficulty data
    sealed class DiffObject
    {
        readonly List<DiffObject> all;
        public readonly HitObject Base, Last;
        public readonly int Index;
        public readonly double ClockRate, DeltaTime, StartTime, EndTime, StrainTime, EndDeltaTime, HitWindowGreat;
        public readonly int Preempt; // map ms (before the rate)
        public readonly float Radius;
        public readonly Vector2 Pos;
        public double JumpDistance, LazyJumpDistance, MinimumJumpDistance, MinimumJumpTime, TravelDistance, TravelTime;
        public double LazyTravelDistance, LazyTravelTime;
        public Vector2? LazyEndPosition;
        public double? Angle, DirectionAngle;
        public List<Nested>? NestedList;

        public DiffObject(Beatmap map, HitObject o, HitObject last, double rate, double hitWindow, float radius, int preempt, int index, List<DiffObject> all)
        {
            this.all = all;
            (Base, Last, Index, ClockRate) = (o, last, index, rate);
            Pos = o.Pos + map.StackOffset(o);
            DeltaTime = ((int)o.Time - (int)last.Time) / rate;
            StartTime = (int)o.Time / rate;
            EndTime = (int)o.EndTime / rate;
            Radius = radius;
            StrainTime = Math.Max(DeltaTime, 25.0);
            EndDeltaTime = Previous(0) is { } prev ? Math.Max(StartTime - prev.EndTime, 25.0) : StrainTime;
            Preempt = preempt;
            if (o.Type == ObjType.Spinner) hitWindow = 0;
            HitWindowGreat = 2.0 * (hitWindow / rate);
            if (o.Type == ObjType.Slider) SetLazySlider(map);
            SetDistances(map, rate);
        }

        public DiffObject? Previous(int n) { int i = Index - (n + 1); return i >= 0 && i < all.Count ? all[i] : null; }
        public DiffObject? Next(int n) { int i = Index + (n + 1); return i >= 0 && i < all.Count ? all[i] : null; }
        public double PreemptScaled => Preempt / ClockRate;
        public double SmallCircleBonus => Math.Max(1.0, 1.0 + (double)((30f - Radius) / 70f));
        public double OdEquivalent => (79.5 - HitWindowGreat / 2.0) / 6.0;
        bool IsSpinner => Base.Type == ObjType.Spinner;
        bool IsSlider => Base.Type == ObjType.Slider;

        // how visible this object is at a time (map ms): fading in from preempt over 400ms (shorter for high AR)
        public double Opacity(double time, bool hidden)
        {
            int start = (int)Base.Time;
            if (time > start) return 0.0;
            double fadeInStart = start - Preempt, fadeIn = 400.0 * Math.Min(1.0, Preempt / 450.0);
            if (hidden)
            {
                double fadeOutStart = start - Preempt + fadeIn, fadeOut = Preempt * 0.3;
                return Math.Min(Clamp01((time - fadeInStart) / fadeIn), 1.0 - Clamp01((time - fadeOutStart) / fadeOut));
            }
            return Clamp01((time - fadeInStart) / fadeIn);
        }

        public double Doubletapness(DiffObject? other)
        {
            if (other == null) return 0.0;
            double currDelta = Math.Max(1.0, DeltaTime), deltaDiff = Math.Abs(Math.Max(1.0, other.DeltaTime) - currDelta);
            double speedRatio = currDelta / Math.Max(currDelta, deltaDiff);
            double windowRatio = Pow(Math.Min(1.0, currDelta / HitWindowGreat), 5);
            double distanceFactor = Pow(Linear(LazyJumpDistance, 100.0, 50.0), 2);
            return 1.0 - Math.Pow(speedRatio, distanceFactor * (1.0 - windowRatio));
        }

        public static Vector2 CursorEnd(DiffObject d) => d.LazyEndPosition ?? d.Pos;

        void SetLazySlider(Beatmap map)
        {
            var o = Base;
            NestedList = NestedPoints(map, o);
            int duration = (int)o.EndTime - (int)o.Time;
            double trackingEnd = Math.Max((double)((int)o.Time + duration) - 36.0, (double)(int)o.Time + (double)duration / 2.0);
            var list = NestedList.ToList();
            int lastTick = list.FindLastIndex(n => n.Kind == NestedKind.Tick);
            if (lastTick >= 0 && list[lastTick].Time > trackingEnd)
            {
                trackingEnd = list[lastTick].Time;
                var t = list[lastTick];
                list.RemoveAt(lastTick);
                list.Add(t);
            }
            LazyTravelTime = trackingEnd - (int)o.Time;
            int spanDuration = duration / o.Slides;
            double endProgress = LazyTravelTime / spanDuration;
            endProgress = endProgress % 2.0 >= 1.0 ? 1.0 - endProgress % 1.0 : endProgress % 1.0;
            // (stable's path has moved with the stack by now while its base position hasn't: the offset counts twice)
            LazyEndPosition = Pos + (o.PointAt((float)(Clamp01(endProgress) * o.Length)) - o.Pos) + map.StackOffset(o);
            Vector2 cursor = Pos;
            double scale = 50f / Radius;
            for (int i = 1; i < list.Count; i++)
            {
                var cur = list[i];
                Vector2 movement = cur.Pos - cursor;
                double movementLength = scale * movement.Length(), required = 90.0;
                if (i == list.Count - 1)
                {
                    Vector2 lazy = LazyEndPosition.Value - cursor;
                    if (lazy.Length() < movement.Length()) movement = lazy;
                    movementLength = scale * movement.Length();
                }
                else if (cur.Kind == NestedKind.Repeat) required = 50.0;
                if (movementLength > required)
                {
                    cursor += movement * (float)((movementLength - required) / movementLength);
                    movementLength *= (movementLength - required) / movementLength;
                    LazyTravelDistance += (float)movementLength;
                }
                if (i == list.Count - 1) LazyEndPosition = cursor;
            }
        }

        // stable counts a slider's repeats from its judging checkpoints: every (checkpoints / spans)-th but the last
        static int RepeatCount(HitObject o)
        {
            int perSpan = o.Checkpoints.Count / o.Slides, n = 0;
            if (perSpan > 0) for (int k = 0; k < o.Checkpoints.Count - 1; k++) if ((k + 1) % perSpan == 0) n++;
            return n;
        }

        void SetDistances(Beatmap map, double rate)
        {
            if (IsSlider)
            {
                TravelDistance = LazyTravelDistance * Math.Max(1.0, Math.Pow(RepeatCount(Base), 0.3));
                TravelTime = Math.Max(LazyTravelTime / rate, 25.0);
            }
            MinimumJumpTime = StrainTime;
            if (IsSpinner || Last.Type == ObjType.Spinner) return;
            float scale = 50f / Radius;
            var prev = Previous(0);
            var prev2 = Previous(1);
            Vector2 lastPos = Last.Pos + map.StackOffset(Last);
            Vector2 lastCursor = prev != null ? CursorEnd(prev) : lastPos;
            JumpDistance = (lastPos - Pos).Length() * scale;
            LazyJumpDistance = (Pos - lastCursor).Length() * scale;
            MinimumJumpDistance = LazyJumpDistance;
            if (Last.Type == ObjType.Slider && prev != null)
            {
                double lastTravelTime = Math.Max(prev.LazyTravelTime / rate, 25.0);
                MinimumJumpTime = Math.Max(StrainTime - lastTravelTime, 25.0);
                float tailJump = (prev.NestedList!.Single(n => n.Kind == NestedKind.Tail).Pos - Pos).Length() * scale;
                MinimumJumpDistance = Math.Max(0.0, Math.Min(LazyJumpDistance - 30.00000762939453, tailJump - 120.00001f));
            }
            if (prev2 != null && !prev2.IsSpinner)
            {
                if (prev != null && prev.IsSlider && prev.TravelDistance > 0.0) lastCursor = prev.Pos;
                Vector2 lastLastCursor = CursorEnd(prev2);
                double a1 = AngleAt(Pos, lastCursor, lastLastCursor);
                double a2 = AltAngle(prev!, lastLastCursor);
                Vector2 v = Pos - lastCursor;
                DirectionAngle = Math.Atan2(Math.Abs(v.Y), Math.Abs(v.X));
                Angle = Math.Min(a1, a2);
            }
        }

        double AltAngle(DiffObject prev, Vector2 lastLast)
        {
            Vector2 vertex = CursorEnd(prev);
            if (prev.IsSlider && prev.TravelDistance > 0.0) lastLast = prev.NestedList![prev.NestedList.Count - 2].Pos;
            return AngleAt(Pos, vertex, lastLast);
        }

        static double AngleAt(Vector2 p, Vector2 vertex, Vector2 q)
        {
            Vector2 v1 = q - vertex, v2 = p - vertex;
            float dot = Vector2.Dot(v1, v2);
            return Math.Abs(Math.Atan2(v1.X * v2.Y - v1.Y * v2.X, dot));
        }
    }

    // ---- evaluators
    static double WideAngleBonus(double angle) => Smoothstep(angle, Rad(40.0), Rad(140.0));
    static double AcuteAngleBonus(double angle) => Smoothstep(angle, Rad(140.0), Rad(40.0));

    static double AimEvaluate(DiffObject c, bool withSliders)
    {
        if (c.Base.Type == ObjType.Spinner || c.Index <= 1 || c.Previous(0)!.Base.Type == ObjType.Spinner) return 0.0;
        var last = c.Previous(0)!;
        var lastLast = c.Previous(2);
        double currDist = withSliders ? c.LazyJumpDistance : c.JumpDistance;
        double currVelocity = currDist / c.StrainTime;
        if (last.Base.Type == ObjType.Slider && withSliders)
            currVelocity = Math.Max(currVelocity, (last.LazyTravelDistance + c.LazyJumpDistance) / c.StrainTime);
        double prevDist = withSliders ? last.LazyJumpDistance : last.JumpDistance;
        double prevVelocity = prevDist / last.StrainTime;
        double strain = currVelocity;
        strain *= AngleRepetition(c, last);
        if (c.Angle.HasValue && last.Angle.HasValue)
        {
            double currAngle = c.Angle.Value, lastAngle = last.Angle.Value;
            double angleBase = Math.Min(currVelocity, prevVelocity), acute = 0.0;
            if (Math.Max(c.StrainTime, last.StrainTime) < 1.25 * Math.Min(c.StrainTime, last.StrainTime))
            {
                acute = AcuteAngleBonus(currAngle);
                acute *= 0.08 + 0.92 * (1.0 - Math.Min(acute, Pow(AcuteAngleBonus(lastAngle), 3)));
                acute *= angleBase * Smootherstep(MsToBpm(c.StrainTime, 2), 300.0, 400.0) * Smootherstep(currDist, 0.0, 200.0);
            }
            double wide = WideAngleBonus(currAngle);
            wide *= 0.25 + 0.75 * (1.0 - Math.Min(wide, Pow(WideAngleBonus(lastAngle), 3)));
            double v1 = currDist / Math.Pow(c.StrainTime, 1.45), v2 = prevDist / Math.Pow(last.StrainTime, 1.45);
            if (last.Base.Type == ObjType.Slider && withSliders)
                v1 = Math.Max(v1, (last.LazyTravelDistance + c.LazyJumpDistance) / Math.Pow(c.StrainTime, 1.45));
            wide *= Math.Min(v1, v2);
            if (lastLast != null)
            {
                float d = (lastLast.Pos - last.Pos).Length();
                if (d < 1f) wide *= 1.0 - 0.55 * (double)(1f - d);
            }
            strain += Math.Max(acute * 2.41, wide * 9.67);
            double wiggle = angleBase * Smootherstep(currDist, 50.0, 100.0) * Math.Pow(Linear(currDist, 300.0, 100.0), 1.8)
                * Smootherstep(currAngle, Rad(110.0), Rad(60.0)) * Smootherstep(prevDist, 50.0, 100.0)
                * Math.Pow(Linear(prevDist, 300.0, 100.0), 1.8) * Smootherstep(lastAngle, Rad(110.0), Rad(60.0));
            strain += wiggle * 1.02;
        }
        if (Math.Max(prevVelocity, currVelocity) != 0.0)
        {
            if (withSliders) currVelocity = currDist / c.StrainTime;
            double distRatio = Smoothstep(Math.Abs(prevVelocity - currVelocity) / Math.Max(prevVelocity, currVelocity), 0.0, 1.0);
            double change = Math.Min(125.0 / Math.Min(c.StrainTime, last.StrainTime), Math.Abs(prevVelocity - currVelocity)) * distRatio;
            change *= Pow(Math.Min(c.StrainTime, last.StrainTime) / Math.Max(c.StrainTime, last.StrainTime), 2);
            strain += change * 0.9;
        }
        if (c.Base.Type == ObjType.Slider && withSliders)
        {
            double sliderVelocity = c.TravelDistance / c.TravelTime;
            strain += (sliderVelocity < 1.0 ? sliderVelocity : Math.Pow(sliderVelocity, 0.75)) * 1.5;
        }
        strain *= c.SmallCircleBonus;
        return strain * (1.0 / (1.0 - Math.Pow(0.03, Math.Pow(c.StrainTime / 1000.0, 0.65))));
    }

    static double AngleRepetition(DiffObject c, DiffObject last)
    {
        if (!c.Angle.HasValue || !last.Angle.HasValue) return 1.0;
        double similar = 0.0;
        for (int i = 0; (double)i < 6.0; i++)
        {
            var o = c.Previous(i);
            if (o == null || Math.Max(c.StrainTime, o.StrainTime) > 1.1 * Math.Min(c.StrainTime, o.StrainTime)) break;
            if (o.DirectionAngle.HasValue && c.DirectionAngle.HasValue)
                similar += Math.Cos(8.0 * Math.Min(Rad(11.25), Math.Abs(c.DirectionAngle.Value - o.DirectionAngle.Value)));
        }
        double repetition = Pow(Math.Min(0.5 / similar, 1.0), 2);
        double distanceFactor = Smootherstep(c.LazyJumpDistance, 0.0, 100.0);
        double angleDiff = Math.Cos(2.0 * Math.Min(Rad(45.0), Math.Abs(c.Angle.Value - last.Angle.Value) * distanceFactor));
        double baseNerf = 1.0 - 0.15 * AcuteAngleBonus(last.Angle.Value) * angleDiff;
        return Pow(baseNerf + (1.0 - baseNerf) * repetition * 0.5 * distanceFactor, 2);
    }

    static double FlowEvaluate(DiffObject c)
    {
        if (c.Base.Type == ObjType.Spinner) return 0.0;
        double prevTravel = (c.Index > 0 ? c.Previous(0) : null)?.LazyTravelDistance ?? 0.0;
        return Math.Min(prevTravel + c.LazyJumpDistance, 120.0) / 120.0 * 1000.0 / c.StrainTime * Math.Pow(c.SmallCircleBonus, 1.5)
            * (1.0 / (1.0 - Math.Pow(0.2, c.StrainTime / 1000.0)));
    }

    static double AgilityEvaluate(DiffObject c, bool withSliders)
    {
        if (c.Base.Type == ObjType.Spinner || c.Index <= 1 || c.Previous(0)!.Base.Type == ObjType.Spinner) return 0.0;
        var last = c.Previous(0)!;
        var lastLast = c.Previous(1)!;
        double currDist = withSliders ? c.LazyJumpDistance : c.JumpDistance;
        double prevDist = withSliders ? last.LazyJumpDistance : last.JumpDistance;
        double currVelocity = currDist / c.StrainTime;
        if (last.Base.Type == ObjType.Slider && withSliders)
            currVelocity = Math.Max(currVelocity, (last.LazyTravelDistance + c.LazyJumpDistance) / c.StrainTime);
        double prevVelocity = prevDist / last.StrainTime;
        double strain = currVelocity;
        strain *= Math.Sqrt(c.SmallCircleBonus);
        strain *= 1.0 + Math.Min(0.25, Pow((Math.Max(c.StrainTime, last.StrainTime) - Math.Min(c.StrainTime, last.StrainTime)) / 50.0, 4));
        if (c.Angle.HasValue && last.Angle.HasValue)
        {
            double angleChange = Math.Sin(Math.Abs(c.Angle.Value - last.Angle.Value) / 2.0) * 180.0 / (c.StrainTime * 0.1);
            strain *= 0.8 + Math.Sqrt(angleChange / 270.0);
        }
        double overlap = 1.0;
        if (c.Index > 2) overlap = 1.0 - Overlap(c, last) * Overlap(c, lastLast) * Overlap(last, lastLast);
        if (c.Angle.HasValue) strain += currVelocity * AcuteAngleBonus(c.Angle.Value) * overlap;
        if (Math.Max(prevVelocity, currVelocity) != 0.0)
        {
            if (withSliders) currVelocity = currDist / c.StrainTime;
            double distRatio = Smoothstep(Math.Abs(prevVelocity - currVelocity) / Math.Max(prevVelocity, currVelocity), 0.0, 1.0);
            double change = Math.Min(125.0 / Math.Min(c.StrainTime, last.StrainTime), Math.Abs(prevVelocity - currVelocity));
            strain += change * distRatio * overlap * 0.52;
        }
        if (c.Base.Type == ObjType.Slider && withSliders) strain += c.TravelDistance / c.TravelTime;
        strain = Math.Pow(strain, 1.45);
        return strain * Smootherstep(currDist, 0.0, 50.0);
    }

    static double Overlap(DiffObject a, DiffObject b)
    {
        double d = Vector2.Distance(a.Pos, b.Pos);
        return Math.Clamp(1.0 - Pow(Math.Max(d - (double)a.Radius, 0.0) / (double)a.Radius, 2), 0.0, 1.0);
    }

    static double SpeedEvaluate(DiffObject c)
    {
        if (c.Base.Type == ObjType.Spinner) return 0.0;
        double strainTime = c.StrainTime;
        double doubletap = 1.0 - c.Doubletapness(c.Next(0));
        strainTime /= Math.Clamp(strainTime / c.HitWindowGreat / 0.93, 0.92, 1.0);
        double speedBonus = 0.0;
        if (MsToBpm(strainTime, 4) > 200.0) speedBonus = 0.75 * Pow((BpmToMs(200.0, 4) - strainTime) / 40.0, 2);
        return (1.0 + speedBonus) * 1000.0 / strainTime * (1.0 / (1.0 - Math.Pow(0.3, c.StrainTime / 1000.0))) * doubletap;
    }

    sealed class Island
    {
        public int Delta, Count = 1, Repeats = 1;
        public Island(int delta) => Delta = delta;
        public void AddDelta(int delta) { if (Delta == int.MaxValue) Delta = Math.Max(delta, 25); Count++; }
        public bool SimilarPolarity(Island o, double eps) => Count > 1 && o.Count > 1 && Math.Abs(Delta - o.Delta) < eps && Count % 2 == o.Count % 2;
        public bool Same(Island o, double eps) => Math.Abs(Delta - o.Delta) < eps && Count == o.Count;
    }

    static double RatioBonus(double ratio) => 1.0 + 26.0 * Math.Min(0.5, BellCurve(ratio - Math.Truncate(ratio)));

    static double RhythmEvaluate(DiffObject c)
    {
        if (c.Base.Type == ObjType.Spinner) return 0.0;
        double complexity = 0.0, eps = c.HitWindowGreat * 0.3;
        var island = new Island(int.MaxValue);
        var previousIsland = new Island(int.MaxValue);
        var seen = new List<Island>();
        double startRatio = 0.0;
        bool firstDeltaSwitch = false;
        int historyAvailable = Math.Min(c.Index, 32), rhythmStart = 0;
        while (rhythmStart < historyAvailable - 2 && c.StartTime - c.Previous(rhythmStart)!.StartTime < 5000.0) rhythmStart++;
        var prevObj = c.Previous(rhythmStart)!;
        var lastObj = c.Previous(rhythmStart + 1)!;
        for (int i = rhythmStart; i > 0; i--)
        {
            var currObj = c.Previous(i - 1)!;
            if (currObj.Base.Type == ObjType.Spinner) continue;
            double timeDecay = (5000.0 - (c.StartTime - currObj.StartTime)) / 5000.0;
            double currHistoricalDecay = Math.Min((double)(historyAvailable - i) / historyAvailable, timeDecay);
            double currDelta = Math.Max(currObj.DeltaTime, 1E-07), prevDelta = Math.Max(prevObj.DeltaTime, 1E-07);
            double deltaDiff = Math.Abs(prevDelta - currDelta);
            if (island.Delta == int.MaxValue) island = new Island((int)currDelta);
            double deltaRatio = Math.Max(prevDelta, currDelta) / Math.Min(prevDelta, currDelta);
            double differenceMultiplier = Math.Clamp(2.0 - deltaRatio / 8.0, 0.0, 1.0);
            double windowPenalty = Math.Clamp((deltaDiff - eps) / eps, 0.0, 1.0);
            double effectiveRatio = RatioBonus(deltaRatio) * windowPenalty * differenceMultiplier;
            if (prevObj.Base.Type == ObjType.Slider)
            {
                double sliderDelta = currObj.MinimumJumpTime, endDelta = currObj.EndDeltaTime;
                effectiveRatio = Math.Min(Math.Min(RatioBonus(Math.Max(sliderDelta, currDelta) / Math.Min(sliderDelta, currDelta)),
                    RatioBonus(Math.Max(endDelta, currDelta) / Math.Min(endDelta, currDelta))), effectiveRatio);
            }
            if (deltaDiff < eps) island.AddDelta((int)currDelta);
            if (firstDeltaSwitch)
            {
                if (deltaDiff > eps)
                {
                    if (currObj.Base.Type == ObjType.Slider) effectiveRatio *= 0.5;
                    if (island.SimilarPolarity(previousIsland, eps)) effectiveRatio *= 0.5;
                    if (Math.Max(lastObj.DeltaTime, 1E-07) > prevDelta + eps && prevDelta > currDelta + eps) effectiveRatio *= 0.125;
                    if (previousIsland.Count == island.Count) effectiveRatio *= 0.5;
                    if (prevDelta > currDelta + eps) effectiveRatio *= 0.65;
                    bool found = false;
                    foreach (var s in seen)
                    {
                        if (!s.Same(island, eps)) continue;
                        if (previousIsland.Same(island, eps)) s.Repeats++;
                        double power = Logistic(island.Delta, 58.33, 0.24, 2.75);
                        effectiveRatio *= Math.Min(3.0 / s.Repeats, Math.Pow(1.0 / s.Repeats, power));
                        found = true;
                        break;
                    }
                    if (!found && island.Count > 0) seen.Add(island);
                    effectiveRatio *= 1.0 - prevObj.Doubletapness(currObj) * 0.75;
                    complexity = island.Count <= 1 ? complexity + 0.7 * currHistoricalDecay : complexity + Math.Sqrt(effectiveRatio * startRatio) * currHistoricalDecay;
                    startRatio = effectiveRatio;
                    if (prevDelta + eps < currDelta) firstDeltaSwitch = false;
                    previousIsland = island;
                    island = new Island((int)currDelta);
                }
            }
            else if (prevDelta > currDelta + eps)
            {
                firstDeltaSwitch = true;
                if (currObj.Base.Type == ObjType.Slider) effectiveRatio *= 0.6;
                if (prevObj.Base.Type == ObjType.Slider) effectiveRatio *= 0.6;
                startRatio = effectiveRatio;
                island = new Island((int)currDelta);
            }
            lastObj = prevObj;
            prevObj = currObj;
        }
        complexity *= Linear(island.Count, 22.0, 3.0);
        return Math.Sqrt(4.0 + complexity * 0.95) / 2.0;
    }

    static double TimeWeight(double ms) => Math.Clamp(2.0 - ms / 1500.0, 0.0, 1.0);

    static double ReadingEvaluate(DiffObject c, bool hidden)
    {
        if (c.Base.Type == ObjType.Spinner || c.Index == 0) return 0.0;
        var next = c.Next(0);
        double velocity = Math.Max(1.0, c.LazyJumpDistance / c.StrainTime);
        double future = FutureVisibility(c), past = PastDensity(c), angleConsistency = AngleConsistency(c);
        double density = HighDensity(next, velocity, angleConsistency, past, future);
        double hiddenValue = hidden ? HiddenDifficulty(c, past, future, velocity, angleConsistency) : 0.0;
        double lowAr = Math.Pow((500.0 - c.PreemptScaled + Math.Abs(c.PreemptScaled - 500.0)) / 2.0, 2.5) / 140000.0 * (angleConsistency * velocity);
        return PNorm(1.5, lowAr, hiddenValue, density) * (1.0 / (1.0 - Math.Pow(0.8, c.StrainTime / 1000.0)));
    }

    static double HighDensity(DiffObject? next, double velocity, double angleConsistency, double past, double future)
    {
        double f = Math.Sqrt(future);
        if (next != null) f *= Smootherstep(next.LazyJumpDistance, 15.0, 150.0);
        double v = Math.Pow(past + f, 1.7) * 0.4 * angleConsistency * velocity;
        v = Math.Max(0.0, v - 2.5);
        return Math.Pow(v, 0.45) * 2.4;
    }

    static double HiddenDifficulty(DiffObject c, double past, double future, double velocity, double angleConsistency)
    {
        double timeSpent = Math.Pow(c.PreemptScaled, 2.2) * 0.01;
        double densityPart = Math.Pow(future + past, 3.3) * 3.0;
        double v = (timeSpent + densityPart) * angleConsistency * velocity * 0.01;
        v = Math.Pow(v, 0.4) * 0.28;
        var prev = c.Previous(0)!;
        if (c.LazyJumpDistance == 0.0 && c.Opacity((int)prev.Base.Time, hidden: true) == 0.0 && prev.StartTime > c.StartTime - c.PreemptScaled)
            v += 700.0000000000001 / Math.Pow(c.StrainTime, 1.5);
        return v;
    }

    static double PastDensity(DiffObject c)
    {
        double sum = 0.0;
        for (int i = 0; i < c.Index; i++)
        {
            var o = c.Previous(i);
            if (o == null || c.StartTime - o.StartTime > 3000.0 || o.StartTime < c.StartTime - c.PreemptScaled) break;
            double v = c.Opacity((int)o.Base.Time, hidden: false) * Smootherstep(o.LazyJumpDistance, 15.0, 150.0);
            sum += v * TimeWeight(c.StartTime - o.StartTime);
        }
        return sum;
    }

    static double FutureVisibility(DiffObject c)
    {
        double sum = 0.0;
        for (var o = c.Next(0); o != null && !(o.StartTime - c.StartTime > 3000.0) && !(c.StartTime < o.StartTime - o.PreemptScaled); o = o.Next(0))
            sum += o.Opacity((int)c.Base.Time, hidden: false) * TimeWeight(o.StartTime - c.StartTime);
        return sum;
    }

    static double AngleConsistency(DiffObject c)
    {
        double sum = 0.0, elapsed = 0.0;
        int k = 0;
        DiffObject cur = c;
        DiffObject? p1 = null, p2 = null;
        while (elapsed < 2000.0)
        {
            var o = c.Previous(k);
            if (o == null) break;
            double weight = 1.0 - Linear(o.StrainTime, 200.0, 2000.0);
            if (o.Angle.HasValue && c.Angle.HasValue)
            {
                double diff = Math.Abs(c.Angle.Value - o.Angle.Value), alt = Math.PI;
                if (cur.Angle.HasValue && p1 != null && p1.Angle.HasValue && p2 != null && p2.Angle.HasValue)
                {
                    alt = Math.Abs(p1.Angle.Value - o.Angle.Value);
                    alt += Math.Abs(p2.Angle.Value - cur.Angle.Value);
                    double f = 1.0;
                    f *= Linear(Math.Min(o.Angle.Value, cur.Angle.Value) * 180.0 / Math.PI, 20.0, 5.0);
                    f *= Linear(Math.Max(o.Angle.Value, cur.Angle.Value) * 180.0 / Math.PI, 60.0, 120.0);
                    alt = Lerp(Math.PI, 0.1 * alt, f);
                }
                double distance = Smootherstep(o.LazyJumpDistance, 0.0, 50.0);
                sum += Math.Cos(3.0 * Math.Min(Rad(30.0), Math.Min(diff, alt) * distance)) * weight;
            }
            elapsed = c.StartTime - o.StartTime;
            k++;
            p2 = p1;
            p1 = cur;
            cur = o;
        }
        return Math.Clamp(2.0 / sum, 0.2, 1.0);
    }

    // ---- skills
    // Per-object strains summed with weights (1 + c/(1+i)) / (i^p + 1 + c/(1+i)) over the positive ones, highest first.
    abstract class SummedSkill(double c, double p)
    {
        protected readonly List<double> values = [];
        public void Process(DiffObject o) => values.Add(Strain(o));
        protected abstract double Strain(DiffObject o);
        protected virtual List<double> Adjust(List<double> v) => v;
        public double Difficulty()
        {
            if (values.Count == 0) return 0.0;
            var list = Adjust(values);
            double sum = 0.0;
            int i = 0;
            foreach (double v in list.OrderByDescending(x => x).Where(x => x > 0.0))
            {
                sum += v * ((1.0 + c / (1 + i)) / (Math.Pow(i, p) + 1.0 + c / (1 + i)));
                i++;
            }
            return sum;
        }
    }

    sealed class SpeedSkill(Mods mods) : SummedSkill(20.0, 0.9)
    {
        double strain;
        protected override double Strain(DiffObject o)
        {
            if (mods.HasFlag(Mods.Relax)) return 0.0;
            double decay = Math.Pow(0.3, o.StrainTime / 1000.0);
            double value = SpeedEvaluate(o);
            if (mods.HasFlag(Mods.Autopilot)) value *= 0.5;
            strain *= decay;
            strain += value * (1.0 - decay) * 1.16;
            return strain * RhythmEvaluate(o);
        }
    }

    sealed class ReadingSkill(Mods mods) : SummedSkill(1.0, 0.9)
    {
        readonly List<DiffObject> seen = [];
        double strain;
        protected override double Strain(DiffObject o)
        {
            seen.Add(o);
            double decay = Math.Pow(0.8, o.DeltaTime / 1000.0);
            double value = ReadingEvaluate(o, mods.HasFlag(Mods.Hidden));
            if (mods.HasFlag(Mods.Relax)) value *= 0.4;
            if (mods.HasFlag(Mods.Autopilot)) value *= 0.1;
            value *= 0.825 + Math.Pow(Math.Max(0.0, o.OdEquivalent), 2.2) / 1125.0;
            strain *= decay;
            strain += value * (1.0 - decay) * 2.5;
            return strain;
        }

        // the first minute's strains ramp up (the earliest count least)
        protected override List<double> Adjust(List<double> v)
        {
            v = v.Where(x => x > 0.0).ToList();
            int n = 0;
            if (seen.Count > 0)
            {
                double end = seen[0].StartTime + 60000.0;
                foreach (var o in seen) { if (o.StartTime > end) break; n++; }
            }
            for (int i = 0; i < Math.Min(v.Count, n); i++)
                v[i] *= Lerp(0.0, 1.0, Math.Log10(Lerp(1.0, 10.0, Math.Clamp((double)i / n, 0.0, 1.0))));
            return v;
        }
    }

    readonly record struct Peak(double Value, double Length) : IComparable<Peak>
    {
        public Peak(double value, double length, bool round) : this(value, Math.Round(length)) { }
        public int CompareTo(Peak other) => other.Value.CompareTo(Value); // highest first
    }

    // Aim: a continuous strain; peaks are kept per section (400ms, or until a higher strain starts a new one), the
    // highest first, up to 110 sections' worth of time.
    sealed class AimSkill(Mods mods)
    {
        const double DecayWeight = 0.9;
        const int SectionLength = 400;
        readonly double maxStored = 11.0 / (1.0 - DecayWeight);
        readonly List<Peak> peaks = [];
        readonly List<(double Value, double Time)> queue = [];
        double currentPeak, sectionStart, sectionEnd, storedLength, strain;
        bool finished;

        static double Decay(double ms) => Math.Pow(0.2, ms / 1000.0);

        public void Process(DiffObject o)
        {
            if (o.Index == 0)
            {
                sectionStart = o.StartTime;
                sectionEnd = sectionStart + SectionLength;
                currentPeak = StrainAt(o);
                return;
            }
            while (o.StartTime > sectionEnd)
            {
                SavePeak(sectionEnd - sectionStart);
                sectionStart = sectionEnd;
                if (queue.Count > 0)
                {
                    var q = queue[0];
                    queue.RemoveAt(0);
                    sectionEnd = q.Time + SectionLength;
                    currentPeak = strain * Decay(sectionStart - o.Previous(0)!.StartTime);
                    currentPeak = Math.Max(currentPeak, q.Value);
                }
                else
                {
                    sectionEnd = sectionStart + SectionLength;
                    currentPeak = strain * Decay(sectionStart - o.Previous(0)!.StartTime);
                }
            }
            double value = StrainAt(o);
            if (value > currentPeak)
            {
                queue.Clear();
                SavePeak(o.StartTime - sectionStart);
                sectionStart = o.StartTime;
                sectionEnd = sectionStart + SectionLength;
                currentPeak = value;
            }
            else
            {
                while (queue.Count > 0 && queue[^1].Value < value) queue.RemoveAt(queue.Count - 1);
                queue.Add((value, o.StartTime));
            }
        }

        double StrainAt(DiffObject o)
        {
            if (mods.HasFlag(Mods.Autopilot)) return 0.0;
            double decay = Decay(o.StrainTime);
            strain *= decay;
            strain += AimValue(o) * (1.0 - decay);
            return strain;
        }

        double AimValue(DiffObject o)
        {
            double aim = AimEvaluate(o, true) * 70.9, flow = FlowEvaluate(o) * 2.35, agility = AgilityEvaluate(o, true) * 242.0;
            return Combine(aim, flow, agility) * (0.985 + Pow(Math.Max(0.0, o.OdEquivalent), 2) / 4000.0);
        }

        double Combine(double aim, double flow, double agility)
        {
            double snap = PNorm(1.2, aim, flow);
            double ratio = agility / snap;
            double w = ratio == 0.0 ? 0.0 : double.IsNaN(ratio) ? 1.0 : 1.0 / (1.0 + Math.Exp(-7.27 * Math.Log(ratio)));
            if (mods.HasFlag(Mods.Relax)) { snap *= 0.75; agility *= 0.6; }
            return (snap * w + agility * (1.0 - w)) * 1.12;
        }

        void SavePeak(double length)
        {
            int at = peaks.BinarySearch(new Peak(currentPeak, length, true));
            peaks.Insert(at < 0 ? ~at : at, new Peak(currentPeak, length, true));
            storedLength += length;
            while (storedLength > maxStored * SectionLength)
            {
                storedLength -= peaks[^1].Length;
                peaks.RemoveAt(peaks.Count - 1);
            }
        }

        public double Difficulty()
        {
            if (!finished) { SavePeak(sectionEnd - sectionStart); finished = true; }
            double sum = 0.0, time = 0.0;
            foreach (var p in Extended())
            {
                double start = time, end = time + p.Length / SectionLength;
                sum += p.Value * (Math.Pow(DecayWeight, start) - Math.Pow(DecayWeight, end));
                time = end;
            }
            return sum / (1.0 - DecayWeight);
        }

        // the hardest 4 seconds count less towards the start (cut into 20ms pieces scaled 0.727 .. 1)
        IEnumerable<Peak> Extended()
        {
            var list = peaks.Where(p => p.Value > 0.0).ToList();
            double total = 0.0;
            int i;
            for (i = 0; list.Count > i; i++)
            {
                if (!(total < 4000.0)) break;
                var p = list[i];
                for (double t = 0.0; t < p.Length; t += 20.0)
                {
                    double scale = Math.Log10(Lerp(1.0, 10.0, Math.Clamp((total + t) / 4000.0, 0.0, 1.0)));
                    list.Add(new Peak(p.Value * Lerp(0.727, 1.0, scale), Math.Min(20.0, p.Length - t), true));
                }
                total += p.Length;
            }
            return list.Skip(i).OrderByDescending(p => p.Value);
        }
    }

    sealed class Calculation(Beatmap map, Mods mods)
    {
        public double Stars()
        {
            var objs = map.Objects;
            double rate = mods.HasFlag(Mods.DoubleTime) ? 1.5 : mods.HasFlag(Mods.HalfTime) ? 0.75 : 1.0;
            var aim = new AimSkill(mods);
            var speed = new SpeedSkill(mods);
            var reading = new ReadingSkill(mods);
            if (objs.Count > 0)
            {
                double hitWindow = Math.Floor(Beatmap.Range(map.OD, 80.0, 50.0, 20.0)) - 0.5;
                float radius = map.Radius;
                int preempt = (int)map.Preempt;
                var list = new List<DiffObject>(objs.Count);
                for (int i = 1; i < objs.Count; i++)
                    list.Add(new DiffObject(map, objs[i], objs[i - 1], rate, hitWindow, radius, preempt, list.Count, list));
                foreach (var o in list.OrderBy(o => (int)o.Base.Time))
                {
                    aim.Process(o);
                    speed.Process(o);
                    reading.Process(o);
                }
            }
            double aimRating = Math.Pow(aim.Difficulty(), 0.63) * 0.02275;
            double speedRating = Math.Sqrt(speed.Difficulty()) * 0.0675;
            double readingRating = Math.Sqrt(reading.Difficulty()) * 0.0675;
            double aimPerf = 4.0 * Pow(aimRating, 3), speedPerf = 4.0 * Pow(speedRating, 3), readingPerf = 4.0 * Pow(readingRating, 3);
            const double flashlightPerf = 0.0; // only with Flashlight, which the remake doesn't rate
            double readingFl = readingPerf <= 0.0 ? flashlightPerf : flashlightPerf <= 0.0 ? readingPerf
                : PNorm(1.1, readingPerf, flashlightPerf * Math.Clamp(flashlightPerf / readingPerf, 0.25, 1.0));
            return Math.Pow(PNorm(1.1, aimPerf, speedPerf, readingFl) * 1.12, 1.0 / 3.0);
        }
    }
}
