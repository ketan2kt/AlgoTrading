# Maintained build dependency: picomatch

This is a source-vendored, patched dependency, not an audit exemption or an
upstream release. Sarthico owns maintenance until a reviewed upstream release
can replace it. It is used by Angular/Vite build tools, not deployed with the
static website. Never publish this private package to a registry.

## Provenance

- Base: `picomatch@4.0.7`, MIT, copyright Jon Schlinkert (LICENSE retained).
- Registry tarball SHA-512:
  `qcJu88Q2IWqJsDD529JKMdwGm/dvInW4HvQnRwiH9JtihJvzGOscDtHE3x1pBKeUOTysQ8kVmLnJ2kJu7yhcGA==`.
- Security correction adapted without functional changes from upstream PR267,
  commit `d9a3f12faf186860cfc86c2834c41f38701152f2`:
  https://github.com/micromatch/picomatch/pull/267
- Advisory: https://github.com/advisories/GHSA-fc5q-39xc-vjpf
- Only upstream `lib/parse.js` is modified. The package metadata identifies this
  maintained fork explicitly; all other runtime files retain upstream contents.

## Correction and safety gates

Single-branch repeated wildcard extglobs previously emitted nested unbounded
quantifiers. The parser now collapses equivalent absorbing branches to one
match (optional for a star extglob). Other ambiguous wildcard branches use the
existing risky-pattern literalization behavior. Ordinary globs, literal
repetition, escaped stars and safe slash-delimited repetition remain supported.
The upstream explicit `maxExtglobRecursion: false` opt-out is unchanged; do not
use that opt-out on untrusted patterns.

`npm run check:vendor` verifies source hashes, installed dependency identity,
pattern semantics, and adversarial matching in a subprocess with a hard
deadline. CI runs this gate before build/tests and retains the full npm audit.
The registry audit cannot assess local source: these explicit checks supplement
it rather than claim that an empty audit alone certifies the fork.

On any future advisory affecting picomatch, review whether this fork is affected.
When an official patch is released, replace the override only after these
regressions and the frontend build/tests pass. Do not change the package identity
or manifest merely to make an audit or integrity failure disappear.
