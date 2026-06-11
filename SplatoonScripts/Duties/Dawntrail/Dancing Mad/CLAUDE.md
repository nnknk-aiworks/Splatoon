# Tower-positioning scripts (P2 Forsaken / "Missing" family)

- Reference point is arena center `(100,0,100)`. `MathHelper.GetRelativeAngle(center, pos)` returns degrees: **0 = North (−Z), clockwise, East (+X) = 90**.
- The `TrueNorth` const `(100,0,120)` is actually map-**south** (+Z); it's only the angle origin — do not read it as north.
- Map axes: North=−Z, South=+Z, East=+X, West=−X.
- "Left/right" in these mechanics = facing **center**, not facing north. Facing center, a player's **left = clockwise / increasing bearing**.
- `PositionBasis.LeftTower` = Cone tower, `PositionBasis.RightTower` = Spread tower.
