// This file is intentionally empty.
//
// The AnalyzeScriptBuilder class supported the legacy analyze-state step
// of the wizard (step 6 of the old 10-step pipeline). It has been
// replaced entirely by the function-catalogue runtime — state gathering
// now runs through StateGatherer using the state-kind functions on the
// signature, so no deterministic script builder is needed.
//
// The file is kept as a placeholder so `git rm` can remove it in a
// standalone commit rather than being deleted as a side-effect of the
// runtime rewrite. Nothing references anything that used to live here.

namespace Mazda.Application.Modules.Generations;
