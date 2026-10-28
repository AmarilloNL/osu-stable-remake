namespace StableRemake;

enum HpEvent { Hit300, Hit100, Hit50, Miss, SliderTick, SliderPoint, SliderBreak, SpinnerSpin, SpinnerBonus }

// stable's HP bar: 0..200, drained per ms of map time (not during breaks), refilled by judgements.
// The drain rate and gain multipliers are calibrated per map by simulating a perfect play, so a
// full-combo run never dips below targets that depend on the map's HP setting.
sealed class Health
{
    public const double Max = 200;
    public double Value = Max;
    public readonly double DropRate;
    readonly double hp;
    double mul = 1, mulGeki = 1;

    public Health(Beatmap map, bool halfTime = false)
    {
        hp = map.HP;
        DropRate = Calibrate(map) * (halfTime ? 0.75 : 1); // stable drains a quarter slower under HalfTime
    }

    static double Range(double d, double a, double b, double c) => Beatmap.Range(d, a, b, c);

    public double Gain(HpEvent e) => e switch
    {
        HpEvent.Hit300 => mul * 6,
        HpEvent.Hit100 => mul * Range(hp, 17.6, 2.2, 2.2),
        HpEvent.Hit50 => mul * Range(hp, 3.2, 0.4, 0.4),
        HpEvent.Miss => Range(hp, -6, -25, -40),
        HpEvent.SliderTick => mul * 3,
        HpEvent.SliderPoint => mul * 4,
        HpEvent.SliderBreak => Range(hp, -4, -15, -28),
        HpEvent.SpinnerSpin => mul * 1.7,
        HpEvent.SpinnerBonus => mul * 2,
        _ => 0,
    };

    // End-of-combo bonus, given only when the combo's last object is hit: geki (all 300s), katu (no 50s or
    // misses), mu otherwise - misses earlier in the combo still get mu, like stable.
    public double ComboEnd(int worst, bool lastHit) => !lastHit ? 0 : worst switch { 300 => mulGeki * 14, 100 => mulGeki * 10, _ => mulGeki * 6 };

    public void Apply(HpEvent e) => Add(Gain(e));
    public void Add(double delta) => Value = Math.Clamp(Value + delta, 0, Max);

    double Calibrate(Beatmap map)
    {
        var objs = map.Objects;
        if (objs.Count == 0) return 0; // nothing to drain against (song select won't start these anyway)
        double rate = 0.05;
        double lowestEver = Range(hp, 195, 160, 60);
        double lowestComboEnd = Range(hp, 198, 170, 80);
        double lowestEnd = Range(hp, 198, 180, 80);
        double recoveryNeeded = Range(hp, 8, 4, 0);

        // TODO: iteration cap guards pathological maps; stable loops until it converges
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            bool retry = false;
            double value = Max, uncapped = Max;
            void Change(double d) { value = Math.Clamp(value + d, 0, Max); uncapped += d; }

            double last = objs[0].Time - map.Preempt;
            int breakIndex = 0, lowComboEnds = 0;
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                double breakTime = 0;
                if (breakIndex < map.Breaks.Count)
                {
                    var b = map.Breaks[breakIndex];
                    if (b.Start >= last && b.End <= o.Time)
                    {
                        breakTime = map.FormatVersion < 8 ? b.End - b.Start : b.End - last;
                        breakIndex++;
                    }
                }
                Change(-rate * (o.Time - last - breakTime));
                last = o.EndTime;
                if (value <= lowestEver) { retry = true; rate *= 0.96; break; }

                double during = rate * (o.EndTime - o.Time);
                double overshoot = Math.Max(0, during - value);
                Change(-during);
                if (o.Type == ObjType.Slider)
                {
                    for (int k = 0; k < o.Slides + 1; k++) Change(mul * 4);
                    for (int k = 0; k < o.Checkpoints.Count(c => c.Kind == CheckpointKind.Tick); k++) Change(mul * 3);
                }
                else if (o.Type == ObjType.Spinner)
                {
                    int spins = (int)((o.EndTime - o.Time) / 1000 * map.SpinsPerSecond);
                    for (int k = 0; k < spins; k++) Change(mul * 1.7);
                }
                if (overshoot > 0 && value - overshoot <= lowestEver) { retry = true; rate *= 0.96; break; }

                bool comboEnd = i == objs.Count - 1 || objs[i + 1].ComboNumber == 1;
                if (comboEnd)
                {
                    Change(mulGeki * 14 + mul * 6);
                    if (value < lowestComboEnd && ++lowComboEnds > 2)
                    {
                        mulGeki *= 1.07; mul *= 1.03;
                        retry = true;
                        break;
                    }
                }
                else Change(mul * 6);
            }

            if (!retry && value < lowestEnd)
            {
                retry = true; rate *= 0.94; mulGeki *= 1.01; mul *= 1.01;
            }
            if (!retry && (uncapped - Max) / objs.Count < recoveryNeeded)
            {
                retry = true; rate *= 0.96; mulGeki *= 1.02; mul *= 1.01;
            }
            if (!retry) break;
        }
        return rate;
    }
}
