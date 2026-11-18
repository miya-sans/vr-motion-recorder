bl_info = {
    "name": "VR Motion Recorder Bridge",
    "author": "VR Motion Recorder",
    "version": (1, 4, 0),
    "blender": (4, 2, 0),
    "location": "View3D > Sidebar > VRMR",
    "description": "Import VR Motion Recorder takes and scenes (your avatar's meshes, armature, face shape keys, cameras), retarget onto your own armature and shape keys, send edits to Unity",
    "category": "Animation",
}

import bpy, json, os, re, math, zlib
from mathutils import Matrix, Vector, Quaternion
from bpy.props import StringProperty, BoolProperty, PointerProperty
from bpy_extras.io_utils import ImportHelper

# ----------------------------------------------------------------------------- name matching

UE_TO_ARKIT = {
    "EyeClosedLeft": "eyeBlinkLeft", "EyeClosedRight": "eyeBlinkRight", "EyeWideLeft": "eyeWideLeft", "EyeWideRight": "eyeWideRight",
    "EyeSquintLeft": "eyeSquintLeft", "EyeSquintRight": "eyeSquintRight", "JawOpen": "jawOpen", "JawLeft": "jawLeft", "JawRight": "jawRight",
    "JawForward": "jawForward", "MouthClosed": "mouthClose", "MouthSmileLeft": "mouthSmileLeft", "MouthSmileRight": "mouthSmileRight",
    "MouthFrownLeft": "mouthFrownLeft", "MouthFrownRight": "mouthFrownRight", "MouthStretchLeft": "mouthStretchLeft", "MouthStretchRight": "mouthStretchRight",
    "MouthDimpleLeft": "mouthDimpleLeft", "MouthDimpleRight": "mouthDimpleRight", "MouthPressLeft": "mouthPressLeft", "MouthPressRight": "mouthPressRight",
    "MouthUpperUpLeft": "mouthUpperUpLeft", "MouthUpperUpRight": "mouthUpperUpRight", "MouthLowerDownLeft": "mouthLowerDownLeft", "MouthLowerDownRight": "mouthLowerDownRight",
    "CheekSquintLeft": "cheekSquintLeft", "CheekSquintRight": "cheekSquintRight", "NoseSneerLeft": "noseSneerLeft", "NoseSneerRight": "noseSneerRight",
    "BrowLowererLeft": "browDownLeft", "BrowLowererRight": "browDownRight", "BrowOuterUpLeft": "browOuterUpLeft", "BrowOuterUpRight": "browOuterUpRight",
    "TongueOut": "tongueOut", "EyeLookInLeft": "eyeLookInLeft", "EyeLookOutLeft": "eyeLookOutLeft", "EyeLookUpLeft": "eyeLookUpLeft",
    "EyeLookDownLeft": "eyeLookDownLeft", "EyeLookInRight": "eyeLookInRight", "EyeLookOutRight": "eyeLookOutRight", "EyeLookUpRight": "eyeLookUpRight",
    "EyeLookDownRight": "eyeLookDownRight",
}


def norm(s):
    s = re.sub(r"[^a-z0-9]", "", s.lower())
    for pre in ("ue", "ft", "v2", "arkit", "blendshape"):
        if s.startswith(pre) and len(s) > len(pre) + 3:
            return s[len(pre):]
    return s


def norm_bone(name):
    """Canonical key for fuzzy bone matching: side + lowercase name without separators/prefixes."""
    s = name.strip()
    side = ""
    m = re.search(r"[._\- ]([LlRr])$", s)
    if m:
        side, s = m.group(1).lower(), s[:m.start()]
    s = re.sub(r"[\s_\-.]", "", s.lower())
    s = re.sub(r"^(mixamorig\d*|bip01|def|jbip[clr]?)", "", s)
    for word, sd in (("left", "l"), ("right", "r")):
        if s.startswith(word):
            side, s = sd, s[len(word):]
        elif s.endswith(word):
            side, s = sd, s[:-len(word)]
    return side + ":" + s


# ----------------------------------------------------------------------------- action compatibility
# Blender 4.4 introduced slotted (layered) Actions and Blender 5.0 removed the legacy Action.fcurves /
# Action.groups API: F-Curves now live in a channelbag per action slot. These helpers give one code path
# for Blender 3.6-4.3 (legacy actions), 4.4-4.5 (both) and 5.x (slotted only).

def fcurves_of(owner, create=False):
    """F-Curve collection of the action assigned to `owner` (an ID with animation data: Object, Key...).
    5.x/4.4+: the channelbag of the assigned slot (created and assigned when create=True); older: Action.fcurves.
    Returns None if there is no action (or, without create, no slot/channelbag yet)."""
    ad = owner.animation_data
    if ad is None or ad.action is None:
        return None
    act = ad.action
    if not hasattr(act, "slots"):  # Blender < 4.4
        return act.fcurves
    slot = ad.action_slot
    if slot is None:
        if not create:
            return None
        slot = next((s for s in act.slots if s.target_id_type in (owner.id_type, 'UNSPECIFIED')), None) \
            or act.slots.new(id_type=owner.id_type, name=owner.name)
        ad.action_slot = slot
    from bpy_extras import anim_utils
    cb = (anim_utils.action_ensure_channelbag_for_slot(act, slot) if create
          else anim_utils.action_get_channelbag_for_slot(act, slot))
    return cb.fcurves if cb else None


def new_fcurve(fcurves, data_path, group=""):
    """fcurves.new() across APIs: channelbags take group_name=, legacy actions take action_group=."""
    try:
        return fcurves.new(data_path, index=0, group_name=group)
    except TypeError:
        return fcurves.new(data_path, index=0, action_group=group)


def assign_new_action(owner, name):
    """Create a fresh action and assign it to `owner` (with a slot on 4.4+), returning its F-Curve collection."""
    if not owner.animation_data:
        owner.animation_data_create()
    owner.animation_data.action = bpy.data.actions.new(name)
    return fcurves_of(owner, create=True)


# ----------------------------------------------------------------------------- import

class VRMR_OT_import(bpy.types.Operator, ImportHelper):
    bl_idname = "vrmr.import_fbx"
    bl_label = "Import VRMR FBX"
    bl_description = "Import an FBX exported by VR Motion Recorder (your avatar's meshes + armature + face shape keys, or armature + face carrier) and set the scene frame range"
    filename_ext = ".fbx"
    filter_glob: StringProperty(default="*.fbx", options={'HIDDEN'})

    import_params: BoolProperty(name="Face/avatar parameters as armature curves", default=True,
                                description="Put the recorded face tracking + avatar parameters on the armature as animated custom properties, in the same action as the body, so edits/retiming keep them in sync")

    def execute(self, context):
        scn = context.scene
        new, arm, carrier, meta = import_vrmr_fbx(context, self.filepath, self.import_params, self.report)
        if arm:
            scn.vrmr_source = arm
        if carrier:
            scn.vrmr_face = carrier
        acts = [o.animation_data.action for o in new if o.animation_data and o.animation_data.action]
        for o in new:
            if o.type == 'MESH' and o.data.shape_keys and o.data.shape_keys.animation_data and o.data.shape_keys.animation_data.action:
                acts.append(o.data.shape_keys.animation_data.action)
        if acts:
            scn.frame_start = int(min(a.frame_range[0] for a in acts))
            scn.frame_end = int(max(a.frame_range[1] for a in acts))
        if not scn.vrmr_clip_name and arm:
            scn.vrmr_clip_name = arm["vrmr_clip"]
        self.report({'INFO'}, "Imported %d objects" % len(new))
        return {'FINISHED'}


PARAM_GROUP = "VRMR Params"


def _reduce(vals, tol=1e-4):
    """Indices of keys needed so linear interpolation reproduces vals within tol (params are mostly flat).
    One pass: from the last key, the slopes that keep every point so far within tol form an interval; a key is set
    where the next point falls outside it (the same keys as checking every segment, in linear time)."""
    n = len(vals)
    if n <= 2:
        return list(range(n))
    keep = [0]
    a = 0
    lo, hi = -math.inf, math.inf
    i = 1
    while i < n:
        d = i - a
        s = (vals[i] - vals[a]) / d
        if lo - 1e-12 <= s <= hi + 1e-12:
            # the segment a -> i is fine; i then has to stay within tol of any longer segment
            lo = max(lo, (vals[i] - tol - vals[a]) / d)
            hi = min(hi, (vals[i] + tol - vals[a]) / d)
            i += 1
        else:
            a = i - 1
            keep.append(a)
            lo, hi = -math.inf, math.inf
    if keep[-1] != n - 1:
        keep.append(n - 1)
    return keep


PARAM_NAMES = "vrmr_param_names"  # armature property: JSON {property name: avatar parameter name} for shortened names


def param_prop(name):
    """Custom property name for an avatar parameter: Blender limits property names to 63 bytes (VRChat parameter names
    can be longer, e.g. Japanese or nested menu paths), so long names are cut and get a hash of the full name."""
    b = name.encode("utf-8")
    if len(b) <= 63:
        return name
    return b[:54].decode("utf-8", errors="ignore") + "~%08x" % (zlib.crc32(b) & 0xffffffff)


def _dp(prop):
    return '["%s"]' % bpy.utils.escape_identifier(prop)


def add_param_curves(arm, params, fps_src, scn):
    """Recorded parameters -> animated custom properties on the armature object (same action as the bones)."""
    if not arm.animation_data:
        arm.animation_data_create()
    if arm.animation_data.action is None:
        fcs = assign_new_action(arm, arm.name + "_VRMR")
    else:
        fcs = fcurves_of(arm, create=True)  # same action + slot as the bones
    k = scn.render.fps / float(fps_src or scn.render.fps)
    lin = bpy.types.Keyframe.bl_rna.properties['interpolation'].enum_items['LINEAR'].value
    count = 0
    try:
        long_names = json.loads(arm.get(PARAM_NAMES, "{}"))
    except Exception:
        long_names = {}
    for name, vals in params.items():
        if not vals:
            continue
        prop = param_prop(name)
        if prop != name:
            long_names[prop] = name
        arm[prop] = float(vals[0])
        dp = _dp(prop)
        fc = fcs.find(dp)
        if fc:
            fcs.remove(fc)
        fc = new_fcurve(fcs, dp, PARAM_GROUP)
        idx = _reduce(vals)
        fc.keyframe_points.add(len(idx))
        co = []
        for i in idx:
            co += [i * k, float(vals[i])]
        fc.keyframe_points.foreach_set("co", co)
        fc.keyframe_points.foreach_set("interpolation", [lin] * len(idx))
        fc.update()
        count += 1
    if long_names:
        arm[PARAM_NAMES] = json.dumps(long_names)
    return count


def param_curves(arm):
    """(avatar parameter name, fcurve) of the parameter curves on the armature's action."""
    fcs = fcurves_of(arm)
    out = []
    try:
        long_names = json.loads(arm.get(PARAM_NAMES, "{}"))
    except Exception:
        long_names = {}
    if fcs:
        for fc in fcs:
            m = re.match(r'\["(.+)"\]$', fc.data_path)
            if m and not m.group(1).startswith("vrmr_"):
                prop = m.group(1).replace('\\"', '"').replace('\\\\', '\\')
                out.append((long_names.get(prop, prop), fc))
    return out


def is_carrier(o):
    """The small stand-in mesh of an armature-only export (its shape keys carry the face); not a real avatar mesh."""
    return o is not None and o.type == 'MESH' and len(o.data.vertices) <= 8


def find_face(new, meta):
    """The mesh whose shape keys carry the face: the avatar's face mesh by name (.face.json "faceMesh"), else the
    carrier, else the mesh with the most animated shape keys."""
    meshes = [o for o in new if o.type == 'MESH' and o.data.shape_keys]
    if not meshes:
        return None
    face_name = (meta.get("faceMesh") or "Body").split("/")[-1]
    for o in meshes:
        if o.name == face_name or o.data.name == face_name:
            return o
    carrier = next((o for o in meshes if is_carrier(o)), None)
    if carrier:
        return carrier

    def keyed(o):
        ad = o.data.shape_keys.animation_data
        if not ad or not ad.action:
            return 0
        fcs = fcurves_of(o.data.shape_keys)
        return len(fcs) if fcs else 0
    return max(meshes, key=keyed)


def import_vrmr_fbx(context, filepath, import_params=True, report=None):
    """Import one VRMR FBX (the avatar's meshes + armature + face shape keys; or armature + face carrier) and its
    .face.json (avatar link, humanoid rig, parameters as armature curves).
    Returns (new objects, armature, face mesh, face.json contents)."""
    scn = context.scene
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=filepath, use_anim=True, anim_offset=0.0, ignore_leaf_bones=False, automatic_bone_orientation=False)
    new = [o for o in bpy.data.objects if o not in before]
    side = os.path.splitext(filepath)[0] + ".face.json"
    meta = {}
    if os.path.exists(side):
        try:
            with open(side, encoding="utf-8") as fh:
                meta = json.load(fh)
        except Exception as e:
            if report:
                report({'WARNING'}, "Could not read %s: %s" % (side, e))
    if meta.get("fps"):
        set_fps(scn, meta["fps"])
    arm = next((o for o in new if o.type == 'ARMATURE'), None)
    carrier = find_face(new, meta)
    if arm:
        arm["vrmr_unity_model"] = meta.get("unityModel", "")
        arm["vrmr_face_mesh"] = meta.get("faceMesh", "Body")
        arm["vrmr_clip"] = meta.get("clip", os.path.splitext(os.path.basename(filepath))[0])
        arm["vrmr_avatar_id"] = meta.get("avatarId", "")
        arm["vrmr_avatar_base"] = meta.get("avatarBase", "")
        # the avatar's humanoid mapping and Unity rest pose (T-pose): "Send to Unity" hands them to the bridge, which
        # then builds the base model's own Humanoid (the armature's rest here is the mesh's bind pose)
        if meta.get("human"):
            arm["vrmr_human"] = json.dumps(meta["human"])
        if meta.get("rest"):
            arm["vrmr_rest"] = json.dumps(meta["rest"])
        if import_params and meta.get("params"):
            n = add_param_curves(arm, meta["params"], meta.get("fps", scn.render.fps), scn)
            if report:
                report({'INFO'}, "%d avatar parameters added as curves on %s" % (n, arm.name))
    return new, arm, carrier, meta


def set_fps(scn, fps):
    """Scene frame rate (fractional rates through fps_base, like Blender's FBX importer)."""
    fps = float(fps or 0) or 60.0
    scn.render.fps = max(1, int(round(fps)))
    scn.render.fps_base = scn.render.fps / fps


# ----------------------------------------------------------------------------- scene import
# A scene export (VR Motion Recorder's NLA scene editor) is one FBX + .face.json per actor and a manifest
# <scene>.vrmrscene.json: fps, frame range (frame 0 = scene start), markers, actors (files, avatar, the scene's
# NLA layout) and cameras (reserved for a future camera system: Unity-space pose + vertical FOV per frame).

def _find_layer_collection(lc, coll):
    if lc.collection == coll:
        return lc
    for c in lc.children:
        r = _find_layer_collection(c, coll)
        if r:
            return r
    return None


def _unity_to_blender(x, y, z):
    # inverse of _unity_pos: Unity (x, y, z) -> Blender (-x, -z, y)
    return Vector((-x, -z, y))


def add_scene_cameras(man, coll):
    """Manifest cameras (Unity-space pose + vertical FOV per frame) -> Blender cameras with keyed transform and lens."""
    lin = bpy.types.Keyframe.bl_rna.properties['interpolation'].enum_items['LINEAR'].value
    out = []
    for c in man.get("cameras", []) or []:
        pos, rot, fov = c.get("pos") or [], c.get("rot") or [], c.get("fov") or []
        n = min(len(pos) // 3, len(rot) // 4, len(fov))
        if n == 0:
            continue
        name = c.get("name") or "Camera"
        cd = bpy.data.cameras.new(name)
        cd.sensor_fit = 'VERTICAL'
        cd.clip_start = float(c.get("near", 0.01))
        cd.clip_end = float(c.get("far", 1000.0))
        ob = bpy.data.objects.new(name, cd)
        coll.objects.link(ob)
        ob.rotation_mode = 'QUATERNION'
        ob["vrmr_camera_id"] = c.get("id", "")
        locs, quats, lens = [], [], []
        prev = None
        for i in range(n):
            locs.append(_unity_to_blender(*pos[i * 3:i * 3 + 3]))
            qu = Quaternion((rot[i * 4 + 3], rot[i * 4], rot[i * 4 + 1], rot[i * 4 + 2]))
            fu, uu = qu @ Vector((0, 0, 1)), qu @ Vector((0, 1, 0))  # Unity camera: looks along +Z, up +Y
            zc = -_unity_to_blender(*fu).normalized()                  # Blender camera: looks along -Z
            yc = _unity_to_blender(*uu).normalized()
            xc = yc.cross(zc).normalized()
            yc = zc.cross(xc)
            q = Matrix((xc, yc, zc)).transposed().to_quaternion()
            if prev is not None:
                q.make_compatible(prev)
            prev = q
            quats.append(q)
            lens.append(cd.sensor_height / (2.0 * math.tan(math.radians(max(0.1, float(fov[i]))) / 2.0)))
        fcs = assign_new_action(ob, name + "_VRMR")
        for dp, comps in (("location", [[v[k] for v in locs] for k in range(3)]), ("rotation_quaternion", [[q[k] for q in quats] for k in range(4)])):
            for k, vals in enumerate(comps):
                try:
                    fc = fcs.new(dp, index=k, group_name="Object Transforms")
                except TypeError:
                    fc = fcs.new(dp, index=k, action_group="Object Transforms")
                _key_all(fc, vals, lin)
        dfcs = assign_new_action(cd, name + "_VRMR_lens")
        _key_all(new_fcurve(dfcs, "lens"), lens, lin)
        ob.location, ob.rotation_quaternion, cd.lens = locs[0], quats[0], lens[0]
        out.append(ob)
    return out


def _key_all(fc, vals, lin):
    idx = _reduce(vals, 1e-6)
    fc.keyframe_points.add(len(idx))
    co = []
    for i in idx:
        co += [float(i), float(vals[i])]
    fc.keyframe_points.foreach_set("co", co)
    fc.keyframe_points.foreach_set("interpolation", [lin] * len(idx))
    fc.update()


def add_nla_layout(arm, actor, fps):
    """The scene's tracks/strips as muted NLA tracks on the armature (reference only: each strip plays the baked
    action over its own frames). The baked result stays the active action, so the pose is unchanged."""
    ad = arm.animation_data
    if not ad or not ad.action:
        return 0
    act = ad.action
    count = 0
    for t in actor.get("tracks", []) or []:
        made = []
        for s in t.get("strips", []) or []:
            fs, fe = int(s.get("frameStart", 0)), int(s.get("frameEnd", 0))
            if fe - fs < 1:
                continue
            label = s.get("source") or s.get("name") or "strip"
            extra = []
            if s.get("maskName") and s.get("maskName") != "Full body":
                extra.append(s["maskName"])
            if s.get("blend") == "add":
                extra.append("Add")
            if s.get("kind") == "action":
                extra.append("keys")
            name = label + (" [" + ", ".join(extra) + "]" if extra else "")
            st = None
            for nt in made:
                try:
                    st = nt.strips.new(name, fs, act)
                    break
                except RuntimeError:
                    st = None
            if st is None:
                nt = ad.nla_tracks.new()
                nt.name = "Scene: " + t.get("name", "Track") + ("" if not made else " (%d)" % (len(made) + 1))
                nt.mute = True
                made.append(nt)
                try:
                    st = nt.strips.new(name, fs, act)
                except RuntimeError:
                    continue
            if hasattr(st, "action_slot") and getattr(ad, "action_slot", None) is not None:
                try:
                    st.action_slot = ad.action_slot
                except Exception:
                    pass
            st.action_frame_start = fs
            st.action_frame_end = fe
            st.frame_start = fs
            st.frame_end = fe
            st.blend_in = min(float(s.get("blendIn", 0)) * fps, (fe - fs) / 2)
            st.blend_out = min(float(s.get("blendOut", 0)) * fps, (fe - fs) / 2)
            st.mute = bool(s.get("mute"))
            count += 1
        if t.get("mute"):
            for nt in made:
                nt.name += " (muted in the scene)"
    return count


def import_scene(context, path, import_params=True, nla_layout=True, cameras=True, report=None):
    """Imports a VR Motion Recorder scene export: one collection per actor (armature + action + face carrier),
    scene fps + frame range, markers, the NLA layout (optional), cameras (when the export has any)."""
    with open(path, encoding="utf-8") as fh:
        man = json.load(fh)
    if man.get("kind") != "scene-export":
        raise RuntimeError("Not a VR Motion Recorder scene export: " + path)
    base = os.path.dirname(path)
    scn = context.scene
    fps = float(man.get("fps", 60.0))
    set_fps(scn, fps)
    name = man.get("name", "Scene")
    root = bpy.data.collections.new("VRMR " + name)
    scn.collection.children.link(root)
    view = context.view_layer
    prev_active = view.active_layer_collection
    arms, carriers = [], []
    try:
        for a in man.get("actors", []) or []:
            fbx = a.get("fbx")
            if not fbx:
                continue
            fp = os.path.join(base, fbx)
            if not os.path.exists(fp):
                if report:
                    report({'WARNING'}, "Missing actor file: " + fp)
                continue
            coll = bpy.data.collections.new(a.get("name", "Actor"))
            root.children.link(coll)
            lc = _find_layer_collection(view.layer_collection, coll)
            if lc:
                view.active_layer_collection = lc
            new, arm, carrier, meta = import_vrmr_fbx(context, fp, import_params, report)
            for o in new:  # the importer links to the active collection; make sure every new object is in the actor's
                if coll not in o.users_collection:
                    for c in list(o.users_collection):
                        c.objects.unlink(o)
                    coll.objects.link(o)
            if arm:
                arm["vrmr_actor"] = a.get("name", "")
                arm["vrmr_scene"] = name
                arm["vrmr_fps"] = fps
                if nla_layout:
                    add_nla_layout(arm, a, fps)
                arms.append(arm)
            if carrier:
                if is_carrier(carrier):
                    carrier.name = "%s face" % a.get("name", "Actor")
                carrier["vrmr_actor"] = a.get("name", "")
                carriers.append(carrier)
            if cameras and man.get("cameras"):
                # the manifest's cameras become real cameras below; drop the FBX's copy of the VRChat camera path
                for o in [o for o in new if o.type == 'EMPTY' and o.name.startswith("VRChatCamera") and not o.children]:
                    bpy.data.objects.remove(o, do_unlink=True)
        if cameras and man.get("cameras"):
            cams = add_scene_cameras(man, root)
            if cams and scn.camera is None:
                scn.camera = cams[0]
    finally:
        view.active_layer_collection = prev_active
    set_fps(scn, fps)  # the FBX importer sets it per file; keep the scene's exact rate
    scn.frame_start = int(man.get("frameStart", 0))
    scn.frame_end = int(man.get("frameEnd", scn.frame_start + 250))
    for m in man.get("markers", []) or []:
        scn.timeline_markers.new(m.get("name", "") or "marker", frame=int(m.get("frame", 0)))
    if arms:
        scn.vrmr_source = arms[0]
        scn.vrmr_clip_name = arms[0].get("vrmr_clip", "")
    if carriers:
        scn.vrmr_face = carriers[0]
    scn.frame_set(scn.frame_start)
    return arms, man


class VRMR_OT_import_scene(bpy.types.Operator, ImportHelper):
    bl_idname = "vrmr.import_scene"
    bl_label = "Import VRMR Scene"
    bl_description = "Import a scene exported by VR Motion Recorder (.vrmrscene.json): every actor's armature, action and face, the scene's frame range, fps and markers"
    bl_options = {'REGISTER', 'UNDO'}
    filename_ext = ".json"
    filter_glob: StringProperty(default="*.vrmrscene.json", options={'HIDDEN'})

    import_params: BoolProperty(name="Face/avatar parameters as armature curves", default=True,
                                description="Put each actor's face tracking + avatar parameters on its armature as animated custom properties, in the same action as the body")
    nla_layout: BoolProperty(name="Scene strips as NLA tracks", default=True,
                             description="Show the scene's tracks and strips (cuts, transitions, layers) as muted NLA tracks under each actor, for reference. The evaluated scene stays the active action, so the pose doesn't change")
    import_cameras: BoolProperty(name="Cameras", default=True, description="Cameras in the scene export (keyed position, rotation and focal length)")

    def execute(self, context):
        try:
            arms, man = import_scene(context, self.filepath, self.import_params, self.nla_layout, self.import_cameras, self.report)
        except Exception as e:
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, "Scene '%s': %d actor(s), %d marker(s), frames %d-%d" % (
            man.get("name", ""), len(arms), len(man.get("markers", []) or []), context.scene.frame_start, context.scene.frame_end))
        return {'FINISHED'}


# ----------------------------------------------------------------------------- send to Unity

def _unity_pos(v):
    # verified mapping: Unity (x,y,z) -> Blender (-x,-z,y); inverse:
    return (-v.x, v.z, -v.y)


def _quat_from_basis(r, u, f):
    m00, m01, m02 = r[0], u[0], f[0]
    m10, m11, m12 = r[1], u[1], f[1]
    m20, m21, m22 = r[2], u[2], f[2]
    tr = m00 + m11 + m22
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        q = ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2
        q = (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2
        q = ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11) * 2
        q = ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _norm(a):
    n = math.sqrt(sum(c * c for c in a)) or 1.0
    return tuple(c / n for c in a)


def _unity_camera_rot(mw):
    """Blender camera (looks down -Z, up +Y) world matrix -> Unity camera rotation (looks down +Z)."""
    m3 = mw.to_3x3().normalized()
    fb = m3 @ Vector((0, 0, -1))
    ub = m3 @ Vector((0, 1, 0))
    f = _norm(_unity_pos(fb))
    up = _unity_pos(ub)
    r = _norm(_cross(up, f))      # Unity: right = Cross(up, forward)
    u = _cross(f, r)
    return _quat_from_basis(r, u, f)


def _vertical_fov(cam, scn):
    d = cam.data
    rx = scn.render.resolution_x * scn.render.pixel_aspect_x
    ry = scn.render.resolution_y * scn.render.pixel_aspect_y
    aspect = rx / ry if ry else 1.0
    if d.type == 'ORTHO':
        return 0.0
    if d.sensor_fit == 'VERTICAL':
        return math.degrees(2 * math.atan(d.sensor_height / (2 * d.lens)))
    if d.sensor_fit == 'HORIZONTAL' or aspect >= 1.0:
        h = 2 * math.atan(d.sensor_width / (2 * d.lens))
        return math.degrees(2 * math.atan(math.tan(h / 2) / aspect))
    return math.degrees(2 * math.atan(d.sensor_width / (2 * d.lens)))


def send_to_unity(context, arm, project, clip, cameras):
    scn = context.scene
    assets = os.path.join(project, "Assets")
    if not os.path.isdir(assets):
        raise RuntimeError("Not a Unity project (no Assets folder): " + project)
    out = os.path.join(assets, "VRMotionRecorder", clip)
    os.makedirs(out, exist_ok=True)
    fbx = os.path.join(out, clip + ".fbx")
    fs, fe = scn.frame_start, scn.frame_end
    fps = scn.render.fps / scn.render.fps_base
    # 1) body (+ fingers) as FBX, exact armature names -> Unity paths Armature/Hips/...
    view = context.view_layer
    prev_sel = [o for o in view.objects if o.select_get()]
    prev_act = view.objects.active
    for o in prev_sel:
        o.select_set(False)
    arm.select_set(True)
    view.objects.active = arm
    try:
        bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'ARMATURE'},
                                 apply_unit_scale=True, apply_scale_options='FBX_SCALE_NONE', bake_space_transform=False,
                                 axis_forward='-Z', axis_up='Y', add_leaf_bones=False, primary_bone_axis='Y', secondary_bone_axis='X',
                                 use_armature_deform_only=False, armature_nodetype='NULL', use_custom_props=False,
                                 bake_anim=True, bake_anim_use_all_bones=True, bake_anim_use_nla_strips=False, bake_anim_use_all_actions=False,
                                 bake_anim_force_startend_keying=True, bake_anim_step=1.0, bake_anim_simplify_factor=0.0)
    finally:
        arm.select_set(False)
        for o in prev_sel:
            o.select_set(True)
        view.objects.active = prev_act
    # 2) parameters (face tracking etc.) and cameras, sampled per frame
    params = []
    for name, fc in param_curves(arm):
        params.append({"name": name, "values": [round(fc.evaluate(f), 5) for f in range(fs, fe + 1)]})
    cams = [{"name": c.name, "pos": [], "rot": [], "fov": [], "near": c.data.clip_start, "far": c.data.clip_end} for c in cameras]
    if cams:
        cur = scn.frame_current
        for f in range(fs, fe + 1):
            scn.frame_set(f)
            for c, d in zip(cameras, cams):
                mw = c.matrix_world
                d["pos"] += [round(v, 5) for v in _unity_pos(mw.translation)]
                d["rot"] += [round(v, 6) for v in _unity_camera_rot(mw)]
                d["fov"].append(round(_vertical_fov(c, scn), 4))
        scn.frame_set(cur)
    side = {
        "source": "blender", "clip": clip, "fps": fps, "frameStart": fs, "frameEnd": fe,
        "sourceAvatarModel": arm.get("vrmr_unity_model", ""), "faceMesh": arm.get("vrmr_face_mesh", "Body"),
        "avatarId": arm.get("vrmr_avatar_id", ""), "avatarBase": arm.get("vrmr_avatar_base", ""),
        "params": params, "cameras": cams,
    }
    for key, prop in (("human", "vrmr_human"), ("rest", "vrmr_rest")):
        try:
            if arm.get(prop):
                side[key] = json.loads(arm[prop])
        except Exception:
            pass
    with open(os.path.join(out, clip + ".vrmr.json"), "w", encoding="utf-8") as fh:
        json.dump(side, fh)
    return out, len(params), len(cams)


class VRMR_OT_send_unity(bpy.types.Operator):
    bl_idname = "vrmr.send_unity"
    bl_label = "Send to Unity"
    bl_description = "Export the edited armature (body + fingers), its face/avatar parameter curves and the scene cameras into the Unity project; the VR Motion Bridge script turns them into a humanoid clip, face + parameter clips, camera clips and a Timeline"

    def execute(self, context):
        scn = context.scene
        arm = scn.vrmr_source if scn.vrmr_source and scn.vrmr_source.type == 'ARMATURE' else context.active_object
        if not arm or arm.type != 'ARMATURE':
            self.report({'ERROR'}, "Pick the armature (Source) in the VRMR panel")
            return {'CANCELLED'}
        project = bpy.path.abspath(scn.vrmr_unity_project).rstrip("\\/")
        if not project:
            self.report({'ERROR'}, "Set the Unity project folder in the VRMR panel")
            return {'CANCELLED'}
        clip = re.sub(r'[\\/:*?"<>|]', "_", scn.vrmr_clip_name or arm.get("vrmr_clip", arm.name)).strip() or "Clip"
        cams = [o for o in scn.objects if o.type == 'CAMERA' and (o.select_get() or o == scn.camera)] if scn.vrmr_send_cameras else []
        try:
            out, n_params, n_cams = send_to_unity(context, arm, project, clip, cams)
        except Exception as e:
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, "Sent '%s' to %s (%d parameter curves, %d cameras)" % (clip, out, n_params, n_cams))
        return {'FINISHED'}


# ----------------------------------------------------------------------------- retarget

def build_map(src, dst):
    exact = {b.name: b.name for b in src.data.bones if b.name in dst.data.bones}
    if len(exact) >= max(8, len(src.data.bones) // 3):
        return exact
    dn = {norm_bone(b.name): b.name for b in dst.data.bones}
    m = {}
    for b in src.data.bones:
        k = norm_bone(b.name)
        if k in dn:
            m[b.name] = dn[k]
    return m


class VRMR_OT_retarget(bpy.types.Operator):
    bl_idname = "vrmr.retarget"
    bl_label = "Retarget to Active Armature"
    bl_description = "Bake the source armature's motion onto the active armature (world-space rest deltas, bone names matched)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        scn = context.scene
        src = scn.vrmr_source
        dst = context.active_object
        if not src or src.type != 'ARMATURE' or not dst or dst.type != 'ARMATURE' or dst == src:
            self.report({'ERROR'}, "Pick the VRMR source armature in the panel and make your armature active")
            return {'CANCELLED'}
        bmap = build_map(src, dst)
        if not bmap:
            self.report({'ERROR'}, "No matching bone names")
            return {'CANCELLED'}
        n = retarget(context.scene, src, dst, bmap)
        self.report({'INFO'}, "Retargeted %d bones over %d frames" % (len(bmap), n))
        return {'FINISHED'}


def _rot(m):
    return m.to_3x3().normalized()


def retarget(scn, src, dst, bmap):
    """World-space rest-delta retarget: target_world_rot = (src_world_rot @ src_rest_rot^-1) @ dst_rest_rot.
    Works across different bone rolls/rest poses as long as the rest shapes correspond (both T-pose, or same avatar)."""
    srest = {b.name: _rot(src.matrix_world @ b.matrix_local) for b in src.data.bones}
    drest = {b.name: _rot(dst.matrix_world @ b.matrix_local) for b in dst.data.bones}
    hips_s = next((n for n in bmap if re.search(r"hips|pelvis", n, re.I)), None) or next(iter(bmap))
    hips_d = bmap.get(hips_s)
    sh = (src.matrix_world @ src.data.bones[hips_s].head_local).z - src.matrix_world.translation.z
    dh = (dst.matrix_world @ dst.data.bones[hips_d].head_local).z - dst.matrix_world.translation.z if hips_d else sh
    k = dh / sh if abs(sh) > 1e-4 else 1.0
    order = sorted((b.name for b in dst.data.bones), key=lambda nm: len(dst.data.bones[nm].parent_recursive))
    inv_map = {v: kk for kk, v in bmap.items()}
    assign_new_action(dst, dst.name + "_VRMR")
    dw_rot_inv = _rot(dst.matrix_world).inverted()
    dw_inv = dst.matrix_world.inverted()
    for pb in dst.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    f0, f1 = scn.frame_start, scn.frame_end
    for f in range(f0, f1 + 1):
        scn.frame_set(f)
        arm = {}
        for name in order:
            b = dst.data.bones[name]
            base = (arm[b.parent.name] @ b.parent.matrix_local.inverted() @ b.matrix_local) if b.parent else b.matrix_local.copy()
            sname = inv_map.get(name)
            if not sname:
                arm[name] = base
                continue
            sw = src.matrix_world @ src.pose.bones[sname].matrix
            world_rot = (_rot(sw) @ srest[sname].inverted()) @ drest[name]
            arm_rot = (dw_rot_inv @ world_rot).to_4x4()
            if name == hips_d:
                p_world = dst.matrix_world.translation + (sw.translation - src.matrix_world.translation) * k
                pos = dw_inv @ p_world
            else:
                pos = base.translation
            M = Matrix.Translation(pos) @ arm_rot
            arm[name] = M
            basis = base.inverted() @ M
            pb = dst.pose.bones[name]
            pb.rotation_quaternion = basis.to_quaternion()
            pb.keyframe_insert("rotation_quaternion", frame=f, group=name)
            if name == hips_d:
                pb.location = basis.to_translation()
                pb.keyframe_insert("location", frame=f, group=name)
    return f1 - f0 + 1


# ----------------------------------------------------------------------------- face

def find_key(kb, name):
    if name in kb:
        return kb[name]
    n = norm(name)
    alias = UE_TO_ARKIT.get(name)
    for k in kb:
        if norm(k.name) == n or (alias and norm(k.name) == norm(alias)):
            return k
    return None


class VRMR_OT_face_from_carrier(bpy.types.Operator):
    bl_idname = "vrmr.face_from_carrier"
    bl_label = "Copy Face From Carrier Mesh"
    bl_description = "Copy shape key animation from the imported VRMR face mesh (or face carrier) to the active mesh (names matched, UE/ARKit aliases)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        dst = context.active_object
        src = context.scene.vrmr_face
        if not dst or dst.type != 'MESH' or not dst.data.shape_keys:
            self.report({'ERROR'}, "Make your face mesh (with shape keys) active")
            return {'CANCELLED'}
        sfcs = fcurves_of(src.data.shape_keys) if src and src.data.shape_keys else None
        if not sfcs:
            self.report({'ERROR'}, "Pick the VRMR face carrier mesh in the panel")
            return {'CANCELLED'}
        kb = dst.data.shape_keys.key_blocks
        fcs = assign_new_action(dst.data.shape_keys, dst.name + "_VRMR_face")
        n = 0
        for fc in sfcs:
            m = re.match(r'key_blocks\["(.+)"\]\.value', fc.data_path)
            if not m:
                continue
            k = find_key(kb, m.group(1))
            if not k:
                continue
            nfc = new_fcurve(fcs, 'key_blocks["%s"].value' % k.name)
            nfc.keyframe_points.add(len(fc.keyframe_points))
            for i, p in enumerate(fc.keyframe_points):
                nfc.keyframe_points[i].co = p.co
                nfc.keyframe_points[i].interpolation = 'LINEAR'
            n += 1
        self.report({'INFO'}, "Copied %d shape key curves" % n)
        return {'FINISHED'}


class VRMR_OT_face_from_json(bpy.types.Operator, ImportHelper):
    bl_idname = "vrmr.face_from_json"
    bl_label = "Load Face .json"
    bl_description = "Key the active mesh's shape keys from a VR Motion Recorder .face.json"
    filename_ext = ".json"
    filter_glob: StringProperty(default="*.json", options={'HIDDEN'})

    def execute(self, context):
        dst = context.active_object
        if not dst or dst.type != 'MESH' or not dst.data.shape_keys:
            self.report({'ERROR'}, "Make your face mesh (with shape keys) active")
            return {'CANCELLED'}
        with open(self.filepath, encoding="utf-8") as fh:
            d = json.load(fh)
        fps = d.get("fps", 60.0)
        scn = context.scene
        scale = scn.render.fps / fps
        kb = dst.data.shape_keys.key_blocks
        fcs = assign_new_action(dst.data.shape_keys, dst.name + "_VRMR_face")
        n = 0
        for name, vals in d.get("shapes", {}).items():
            k = find_key(kb, name)
            if not k:
                continue
            fc = new_fcurve(fcs, 'key_blocks["%s"].value' % k.name)
            fc.keyframe_points.add(len(vals))
            for i, v in enumerate(vals):
                fc.keyframe_points[i].co = (scn.frame_start + i * scale, v)
                fc.keyframe_points[i].interpolation = 'LINEAR'
            n += 1
        scn.frame_end = max(scn.frame_end, int(scn.frame_start + d.get("frames", 0) * scale))
        self.report({'INFO'}, "Keyed %d shape keys" % n)
        return {'FINISHED'}


# ----------------------------------------------------------------------------- UI

class VRMR_PT_panel(bpy.types.Panel):
    bl_label = "VR Motion Recorder"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "VRMR"

    def draw(self, context):
        l = self.layout
        scn = context.scene
        l.operator("vrmr.import_fbx", icon='IMPORT')
        l.operator("vrmr.import_scene", icon='SCENE_DATA')
        box = l.box()
        box.label(text="Send to Unity")
        box.prop(scn, "vrmr_unity_project", text="Project")
        box.prop(scn, "vrmr_clip_name", text="Clip")
        box.prop(scn, "vrmr_send_cameras", text="Active + selected cameras")
        box.operator("vrmr.send_unity", icon='EXPORT')
        if scn.vrmr_source and scn.vrmr_source.type == 'ARMATURE':
            n = len(param_curves(scn.vrmr_source))
            box.label(text="%d parameter curves on %s" % (n, scn.vrmr_source.name), icon='INFO')
        box = l.box()
        box.label(text="Body")
        box.prop(scn, "vrmr_source", text="Source")
        box.label(text="Active object = target armature")
        box.operator("vrmr.retarget", icon='ARMATURE_DATA')
        box = l.box()
        box.label(text="Face")
        box.prop(scn, "vrmr_face", text="Face")
        box.label(text="Active object = your face mesh")
        box.operator("vrmr.face_from_carrier", icon='SHAPEKEY_DATA')
        box.operator("vrmr.face_from_json", icon='FILE')


def _arm_poll(self, obj):
    return obj.type == 'ARMATURE'


def _mesh_poll(self, obj):
    return obj.type == 'MESH' and obj.data.shape_keys is not None


classes = (VRMR_OT_import, VRMR_OT_import_scene, VRMR_OT_send_unity, VRMR_OT_retarget, VRMR_OT_face_from_carrier, VRMR_OT_face_from_json, VRMR_PT_panel)


def register():
    for c in classes:
        bpy.utils.register_class(c)
    bpy.types.Scene.vrmr_source = PointerProperty(type=bpy.types.Object, poll=_arm_poll)
    bpy.types.Scene.vrmr_face = PointerProperty(type=bpy.types.Object, poll=_mesh_poll)
    bpy.types.Scene.vrmr_unity_project = StringProperty(name="Unity project", subtype='DIR_PATH', default="")
    bpy.types.Scene.vrmr_clip_name = StringProperty(name="Clip name", default="")
    bpy.types.Scene.vrmr_send_cameras = BoolProperty(name="Send cameras", default=True)


def unregister():
    del bpy.types.Scene.vrmr_source
    del bpy.types.Scene.vrmr_face
    del bpy.types.Scene.vrmr_unity_project
    del bpy.types.Scene.vrmr_clip_name
    del bpy.types.Scene.vrmr_send_cameras
    for c in reversed(classes):
        bpy.utils.unregister_class(c)


if __name__ == "__main__":
    register()
