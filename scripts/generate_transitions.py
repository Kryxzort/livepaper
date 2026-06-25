#!/usr/bin/env python3
"""Bundle gl-transitions GLSL into livepaper's transitions/ dir.

For each transition: strip Author/License header (-> manifest), turn each
`uniform <type> <name>; // = <default>` into `const <type> <name> = <coerced>;`
(so both the C/GLES3 renderer and the WebGL2 preview need ZERO uniform code),
wrap with the standard header, and validate via glslangValidator. Failures and
effects needing extra sampler inputs are dropped.
"""
import os, re, json, subprocess, sys, tempfile

# usage: generate_transitions.py <path-to-gl-transitions/transitions>
#   clone https://github.com/gl-transitions/gl-transitions first.
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "gl-transitions", "transitions")
OUT = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "transitions"))
GLSL_OUT = os.path.join(OUT, "glsl")

# GLES 3.00 / WebGL2 wrapper. //<<BODY>> = the transition body (uniforms intact).
# relocate_globals() moves every non-constant global initializer INTO the one function that uses it
# (transitively resolving chains), so NO effect needs GL_EXT_shader_non_constant_global_initializers
# → all compile in plain ESSL 3.00 / WebGL2 / ANGLE (and desktop GL). No extensions.
WRAP = """#version 300 es
precision highp float;
uniform sampler2D fromTex;
uniform sampler2D toTex;
uniform float progress;
uniform float ratio;
in vec2 vUv;
out vec4 fragColor;
#define texture2D texture
vec4 getFromColor(vec2 uv){ return texture(fromTex, vec2(uv.x, 1.0 - uv.y)); }
vec4 getToColor(vec2 uv){ return texture(toTex, vec2(uv.x, 1.0 - uv.y)); }
//<<BODY>>
void main(){ fragColor = transition(vUv); }
"""

VERT = """#version 300 es
out vec2 vUv;
const vec2 P[3] = vec2[3](vec2(-1.0,-1.0), vec2(3.0,-1.0), vec2(-1.0,3.0));
void main(){ vec2 p = P[gl_VertexID]; vUv = vec2(p.x*0.5+0.5, p.y*0.5+0.5); gl_Position = vec4(p,0.0,1.0); }
"""

# uniform line; default may follow as `// = X`, `//=X`, or `/* = X */`, with the
# `;` either before or after the comment.
UNI = re.compile(r'^\s*uniform\s+(\w+)\s+(\w+)\b.*$')
DEF = re.compile(r'/[/*]+\s*=\s*(.+?)\s*(?:\*/\s*;?\s*)?$')

def parse_default(typ, raw):
    """Comment default string -> list of numbers the host can apply via glUniform."""
    if raw is None: return None
    d = raw.strip().rstrip(';').strip().rstrip('*/').strip()
    if typ == 'bool':
        return [1.0 if d in ('1', 'true', 'True') else 0.0]
    mp = re.search(r'\((.*)\)', d)  # vecN(...)/ivecN(...) -> only the args (skip the type digit)
    if mp: d = mp.group(1)
    nums = [float(x) for x in re.findall(r'-?\d+\.?\d*', d)]
    if not nums: return None
    n = {'float': 1, 'int': 1, 'vec2': 2, 'ivec2': 2, 'vec3': 3, 'vec4': 4}.get(typ)
    if n and len(nums) == 1 and n > 1:  # ivec2(20) -> fills all components
        nums = nums * n
    if typ in ('int', 'ivec2'):
        nums = [int(x) for x in nums]
    return nums

# a depth-0 scalar/vector var declaration WITH an initializer (the thing ESSL 3.00 forbids at
# global scope unless it's a constant expression). const/uniform excluded.
GLOBAL_VARINIT = re.compile(
    r'^\s*(?:highp |mediump |lowp )?'
    r'(?:bool|int|uint|float|vec[234]|ivec[234]|uvec[234]|mat[234])\s+\w+\s*=\s*.+;\s*$')

def _function_bodies(lines):
    """[(open_brace_line, close_brace_line)] for each top-level function body (depth 0→1→0)."""
    bodies, depth, open_idx = [], 0, None
    for i, ln in enumerate(lines):
        for _ in range(ln.count("{")):
            depth += 1
            if depth == 1:
                open_idx = i
        for _ in range(ln.count("}")):
            depth -= 1
            if depth == 0 and open_idx is not None:
                bodies.append((open_idx, i)); open_idx = None
    return bodies

def relocate_globals(body):
    """Move each non-constant global initializer into the ONE function that uses it (as a local) so
    it needs no GL_EXT_shader_non_constant_global_initializers (→ compiles in WebGL2/ANGLE). If a
    global is read at global scope or by more than one function, leave it (the extension covers that
    rare case). Behaviour-preserving — the value is identical, just function-scoped."""
    lines = body.split("\n")
    bodies = _function_bodies(lines)
    if not bodies:
        return body
    in_func = {k for o, c in bodies for k in range(o, c + 1)}
    cand = []  # (idx, text, name) — depth-0 var-inits outside any function
    for idx, ln in enumerate(lines):
        s = ln.strip()
        if idx not in in_func and not s.startswith(("const", "uniform")) and GLOBAL_VARINIT.match(ln):
            cand.append((idx, s, re.match(r'^\s*\S+\s+(\w+)', ln).group(1)))
    cand_idx = {i for i, _, _ in cand}
    if not cand:
        return body
    names = [n for _, _, n in cand]
    pats = {n: re.compile(r'\b' + re.escape(n) + r'\b') for n in names}
    # functions that reference each global DIRECTLY, and which other globals' decls reference it
    direct = {n: {o for (o, c) in bodies if any(pats[n].search(lines[k]) for k in range(o, c + 1))} for n in names}
    refby = {n: set() for n in names}
    for idx, _, name in cand:
        for idx2, _, name2 in cand:
            if idx2 != idx and pats[name].search(lines[idx2]):
                refby[name].add(name2)  # global `name2` reads `name` → `name` must follow name2
    # propagate: a global's target functions = its own ∪ those of every global that reads it
    funcs = {n: set(direct[n]) for n in names}
    changed = True
    while changed:
        changed = False
        for n in names:
            for refr in refby[n]:
                if not funcs[refr].issubset(funcs[n]):
                    funcs[n] |= funcs[refr]; changed = True
    plan = {}  # function-open-line -> [(orig_idx, text)] to inject at its top (file order = dep order)
    for idx, s, name in cand:
        global_use = any(k != idx and k not in in_func and k not in cand_idx and pats[name].search(lines[k]) for k in range(len(lines)))
        if global_use or len(funcs[name]) != 1:
            continue  # used at global scope or by multiple functions → keep global (extension covers it)
        plan.setdefault(next(iter(funcs[name])), []).append((idx, s))
    if not plan:
        return body
    drop = {idx for lst in plan.values() for idx, _ in lst}
    out = []
    for i, ln in enumerate(lines):
        if i in drop:
            continue
        out.append(ln)
        if i in plan:  # function open-brace line → inject its relocated globals right after it
            out.extend("  " + txt for _, txt in plan[i])
    return "\n".join(out)

def transform(text):
    """Strip Author/License/ported headers (-> meta). Keep uniforms verbatim; collect
    their metadata. Returns (body, meta) or (None, reason)."""
    author = license_ = None
    out_lines, uniforms = [], []
    for line in text.splitlines():
        s = line.strip()
        m = re.match(r'^//\s*Author:\s*(.+)', s, re.I)
        if m: author = m.group(1).strip(); continue
        m = re.match(r'^//\s*License:\s*(.+)', s, re.I)
        if m: license_ = m.group(1).strip(); continue
        if re.match(r'^//\s*ported by', s, re.I): continue
        u = UNI.match(line)
        if u:
            typ, name = u.group(1), u.group(2)
            if typ == 'sampler2D':
                return None, "needs extra sampler input"
            dm = DEF.search(line)
            val = parse_default(typ, dm.group(1) if dm else None)
            uniforms.append({"name": name, "type": typ, "default": val})
        out_lines.append(line)  # keep the uniform line verbatim
    body = relocate_globals("\n".join(out_lines).strip())
    return body, {"author": author or "gl-transitions", "license": license_ or "MIT", "uniforms": uniforms}

def validate(body):
    full = WRAP.replace("//<<BODY>>", body)
    with tempfile.NamedTemporaryFile("w", suffix=".frag", delete=False) as f:
        f.write(full); path = f.name
    try:
        r = subprocess.run(["glslangValidator", path], capture_output=True, text=True)
        return r.returncode == 0, (r.stdout + r.stderr)
    finally:
        os.unlink(path)

def pretty(name):
    s = re.sub(r'([a-z0-9])([A-Z])', r'\1 \2', name)
    s = s.replace('_', ' ').replace('-', ' ')
    return ' '.join(w if w.isupper() else w.capitalize() for w in s.split())

def categorize(n):
    n = n.lower()
    def has(*ks): return any(k in n for k in ks)
    if has('fade','dissolve','crossfade'): return 'Fade'
    if has('wipe','slide','door','push','swap','squeeze','dreamy'): return 'Slide & Wipe'
    if has('circle','heart','star','crop','polka','shape','bowtie','angular','radial'): return 'Shapes'
    if has('zoom','scale'): return 'Zoom'
    if has('pixel','mosaic','grid','block','bricks','kaleido'): return 'Pixelate'
    if has('flip','cube','rotate','rotat','book','flyeye','doorway','windowslice'): return '3D & Flip'
    if has('glitch','distort','warp','wave','morph','wind','ripple','water','perlin','swirl','burn','fire','flame','rain','drop'): return 'Distort'
    return 'Other'

DEFAULT_ON = {'fade','CircleCrop','circleopen','GridFlip','pixelize','Swirl','cube',
              'doorway','Radial','wind','crosswarp','morph','ZoomInCircles','LinearBlur',
              'WaterDrop','Dreamy','Bounce','PolkaDotsCurtain','wipeRight'}

def main():
    os.makedirs(GLSL_OUT, exist_ok=True)
    kept, dropped = [], []
    for fn in sorted(os.listdir(SRC)):
        if not fn.endswith('.glsl'): continue
        eid = fn[:-5]
        text = open(os.path.join(SRC, fn)).read()
        body, meta = transform(text)
        if body is None:
            dropped.append((eid, meta)); continue
        ok, log = validate(body)
        if not ok:
            firstline = next((l for l in log.splitlines() if 'ERROR' in l), log.splitlines()[1] if len(log.splitlines())>1 else 'compile fail')
            dropped.append((eid, firstline.strip()[:80])); continue
        open(os.path.join(GLSL_OUT, fn), "w").write(body + "\n")
        kept.append({"id": eid, "name": pretty(eid), "category": categorize(eid),
                     "defaultOn": eid in DEFAULT_ON, "author": meta["author"],
                     "license": meta["license"], "uniforms": meta["uniforms"]})
    kept.sort(key=lambda e: (e["category"], e["name"]))
    open(os.path.join(WRAP_OUT := os.path.join(OUT, "wrap.frag.template")), "w").write(WRAP)
    open(os.path.join(OUT, "wrap.vert"), "w").write(VERT)
    json.dump(kept, open(os.path.join(OUT, "manifest.json"), "w"), indent=2)
    print(f"KEPT {len(kept)}  DROPPED {len(dropped)}")
    print("defaultOn:", sorted(e['id'] for e in kept if e['defaultOn']))
    print("--- dropped ---")
    for eid, why in dropped: print(f"  {eid}: {why}")

if __name__ == "__main__": main()
