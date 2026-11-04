# Storm Script VM semantics (as implemented in `StormScript/Runtime`)

Taken from the open-source engine (`storm-engine/src/libs/core/src/data.cpp`, `internal_functions.cpp`) and verified by
running the shipped scripts.

- **Values**: int (32 bit), float (32 bit), string, and references. `object` variables own an attribute tree; `ref`/`aref`
  point to a tree node (or, for `&x` of a scalar/array, to a variable).
- **Attributes are strings.** `a.b = 5` stores "5" (floats via `gcvt`, 10 significant digits: `1.799999952`). Reading a
  missing attribute yields "". Child order is observable (`GetAttributeN`). All names are case-insensitive.
- **Operators**: `string + anything` concatenates (floats use shortest round-trip form); `==`/`!=` on two strings is
  case-insensitive; `<`/`>` on two strings compare **length only**; string vs number converts the left string; number vs
  string is false. `/` is integer division for two ints. String truthiness: non-empty (so "0" is true).
- **Preprocessor**: `#define` is global and order independent (two passes). Includes are once-only, but a segment's own
  includes are forgotten on `UnloadSegment` so a later `LoadSegment` sees them again.
- **Segments**: `LoadSegment(file)` compiles a file and registers its functions/globals; `UnloadSegment` drops its functions.
- **Events**: `SetEventHandler(evt, fn, prio)`, `Event` (synchronous, returns last handler result), `PostEvent(evt, delayMs, fmt, data...)`,
  `GetEventData()` pops the next datum of the event being handled. `#event_handler(evt,"fn")` registers statically.
- **Engine objects** (`CreateEntity(&obj, "Class")`) are tracked as `Entity` stubs; `SendMessage` is routed to a C# handler per
  class (`Interp.EntityHandlers`) and always counted in `MessageStats`, which is the to-do list for the new engine.

Not yet emulated: engine-side characters/locations/sea/interface classes, saving/loading, `GetAssembledString`, controls.
