### Value editor, pinning, change notifications and change log
# The Values tab mirrors the web editor: a filterable, sortable, paged table with inline editing and a
# change column measured from the moment the menu was opened. Pins and notification settings live in
# persistent, so they survive restarts. Container walks go one level deep (dicts, lists and plain
# objects) and are capped.

init 998 python:
    rcheats_sep = "\x1f"

    def rcheats_repr(k):
        if isinstance(k, rcheats_text_types):
            return "'%s'" % (k,)
        return repr(k)

    def rcheats_is_prim(v):
        return isinstance(v, (bool, float) + rcheats_text_types + rcheats_int_types)

    def rcheats_type_name(v):
        if isinstance(v, bool):
            return "bool"
        if isinstance(v, float):
            return "float"
        if isinstance(v, rcheats_int_types):
            return "int"
        return "text"

    def rcheats_key(path):
        return rcheats_sep.join(path)

    def rcheats_clip(s, n):
        s = "%s" % (s,)
        return s if len(s) <= n else s[:max(1, n - 3)] + "..."

    def rcheats_entries(show_all=False):
        # Returns [(label, path, value)] where path is (name, kind, sub) and kind is "", "k", "i" or "a".
        out = []
        d = renpy.store.__dict__
        for name in sorted(list(d.keys())):
            if name.startswith("_") or name.startswith("rcheats"):
                continue
            if name in rcheats_baseline and not show_all:
                continue
            try:
                v = d[name]
                if rcheats_is_prim(v):
                    if not isinstance(v, rcheats_text_types) or len(v) <= 200:
                        out.append((name, (name, "", ""), v))
                elif isinstance(v, dict):
                    for k, x in list(v.items())[:150]:
                        if rcheats_is_prim(x):
                            out.append(("%s[%s]" % (name, rcheats_repr(k)), (name, "k", rcheats_repr(k)), x))
                elif isinstance(v, list):
                    for i, x in enumerate(v[:150]):
                        if rcheats_is_prim(x):
                            out.append(("%s[%d]" % (name, i), (name, "i", str(i)), x))
                elif hasattr(v, "__dict__") and not callable(v) and not str(getattr(type(v), "__module__", "")).startswith(("renpy", "builtins", "__builtin__")):
                    for a, x in list(vars(v).items())[:150]:
                        if not a.startswith("_") and rcheats_is_prim(x):
                            out.append(("%s.%s" % (name, a), (name, "a", a), x))
            except Exception:
                continue
            if len(out) > 3000:
                break
        return out

    def rcheats_read(path):
        name, kind, sub = path
        try:
            root = getattr(renpy.store, name)
            if kind == "":
                return root
            if kind == "k":
                for k in root:
                    if rcheats_repr(k) == sub:
                        return root[k]
            elif kind == "i":
                return root[int(sub)]
            elif kind == "a":
                return getattr(root, sub)
        except Exception:
            pass
        return rcheats_missing

    def rcheats_write(path, value):
        name, kind, sub = path
        if kind == "":
            setattr(renpy.store, name, value)
            return
        root = getattr(renpy.store, name)
        if kind == "k":
            for k in list(root):
                if rcheats_repr(k) == sub:
                    root[k] = value
                    return
        elif kind == "i":
            root[int(sub)] = value
        elif kind == "a":
            setattr(root, sub, value)

    def rcheats_parse(text, old):
        t = text.strip()
        if isinstance(old, bool):
            l = t.lower()
            if l in ("true", "1", "yes", "on"):
                return True
            if l in ("false", "0", "no", "off"):
                return False
            raise ValueError("not a bool")
        if isinstance(old, rcheats_int_types):
            return int(float(t)) if "." in t else int(t)
        if isinstance(old, float):
            return float(t)
        return text

    def rcheats_commit(path, text, quiet=False):
        old = rcheats_read(path)
        if old is rcheats_missing:
            return False
        try:
            new = rcheats_parse(text, old)
        except Exception:
            if not quiet:
                rcheats_note("{color=#ff6b6b}" + rcheats_zw + "Not a valid value: %s{/color}" % rcheats_esc(text), force=True)
            return False
        rcheats_write(path, new)
        if rcheats_key(path) in persistent.rcheats_pins:
            persistent.rcheats_pins[rcheats_key(path)] = new
        return True

    def rcheats_set_text(path, text):
        rcheats_commit(path, text)
        renpy.restart_interaction()

    def rcheats_toggle_bool(path):
        old = rcheats_read(path)
        if isinstance(old, bool):
            rcheats_commit(path, "false" if old else "true")
        renpy.restart_interaction()

    def rcheats_add(path, delta):
        old = rcheats_read(path)
        if old is rcheats_missing or isinstance(old, bool) or not isinstance(old, rcheats_num_types):
            return
        rcheats_set_text(path, rcheats_fmt(old + delta))

    def rcheats_toggle_pin(path):
        key = rcheats_key(path)
        pins = persistent.rcheats_pins
        if key in pins:
            del pins[key]
        else:
            cur = rcheats_read(path)
            if cur is not rcheats_missing:
                pins[key] = cur
        renpy.save_persistent()
        renpy.restart_interaction()

    def rcheats_clear_pins():
        persistent.rcheats_pins.clear()
        renpy.save_persistent()
        renpy.restart_interaction()

    def rcheats_label_of(path):
        name, kind, sub = path
        if kind in ("k", "i"):
            return "%s[%s]" % (name, sub)
        if kind == "a":
            return "%s.%s" % (name, sub)
        return name

    def rcheats_apply_pins(notify=False):
        pins = persistent.rcheats_pins
        if not pins:
            return
        for key in list(pins.keys()):
            path = tuple(key.split(rcheats_sep))
            want = pins[key]
            cur = rcheats_read(path)
            if cur is rcheats_missing:
                continue
            if cur != want or type(cur) != type(want):
                try:
                    rcheats_write(path, want)
                except Exception:
                    continue
                if notify:
                    rcheats_note(rcheats_zw + "%s is locked at %s (tried %s)" % (rcheats_esc(rcheats_label_of(path)), rcheats_esc(rcheats_fmt(want)), rcheats_esc(rcheats_fmt(cur))))

    # Table view

    def rcheats_mark_base():
        rcheats_state.base = dict((rcheats_key(p), v) for l, p, v in rcheats_entries(True))

    def rcheats_sort_key(row, key):
        if key == "name":
            return row[0].lower()
        if key == "type":
            return row[5]
        if key == "change":
            return abs(row[9])
        v = row[2]
        if isinstance(v, bool):
            return (2, int(v))
        if isinstance(v, rcheats_num_types):
            return (0, float(v))
        return (1, ("%s" % (v,)).lower())

    def rcheats_view():
        ui = rcheats_state.ui
        base = rcheats_state.base or {}
        pins = persistent.rcheats_pins or {}
        q = ui["query"].strip().lower()
        rows = []
        for label, path, v in rcheats_entries(ui["show_all"]):
            key = rcheats_key(path)
            change, text, delta = "", "", 0
            b = base.get(key, rcheats_missing)
            if b is not rcheats_missing:
                if isinstance(v, rcheats_num_types) and not isinstance(v, bool) and isinstance(b, rcheats_num_types) and not isinstance(b, bool):
                    if v != b:
                        delta = v - b
                        change = "up" if delta > 0 else "down"
                        text = ("+" if delta > 0 else "") + rcheats_fmt(delta)
                elif v != b or type(v) != type(b):
                    change, text = "chg", "changed"
            section = path[0] if path[1] else ""
            name = label[len(path[0]):] if path[1] else label
            rows.append((label, path, v, section, name, rcheats_type_name(v), change, text, key in pins, delta, key))

        up = len([r for r in rows if r[6] == "up"])
        down = len([r for r in rows if r[6] == "down"])
        changed = len([r for r in rows if r[6]])

        if ui["type"] != "all":
            rows = [r for r in rows if r[5] == ui["type"]]
        if q:
            rows = [r for r in rows if q in r[0].lower() or q in ("%s" % (r[2],)).lower()]
        if ui["only_pins"]:
            rows = [r for r in rows if r[8]]
        if ui["change"] == "chg":
            rows = [r for r in rows if r[6]]
        elif ui["change"] in ("up", "down"):
            rows = [r for r in rows if r[6] == ui["change"]]
        if ui["sort"] != "order":
            rows.sort(key=lambda r: rcheats_sort_key(r, ui["sort"]), reverse=ui["desc"])

        size = ui["page_size"]
        pages = max(1, (len(rows) + size - 1) // size)
        page = max(0, min(ui["page"], pages - 1))
        return {"rows": rows, "page_rows": rows[page * size:(page + 1) * size], "page": page, "pages": pages,
                "up": up, "down": down, "changed": changed}

    def rcheats_ui_set(key, value):
        rcheats_state.ui[key] = value
        rcheats_state.ui["page"] = 0
        renpy.restart_interaction()

    def rcheats_ui_toggle(key):
        rcheats_state.ui[key] = not rcheats_state.ui[key]
        rcheats_state.ui["page"] = 0
        renpy.restart_interaction()

    def rcheats_sort(key):
        ui = rcheats_state.ui
        if ui["sort"] == key:
            ui["desc"] = not ui["desc"]
        else:
            ui["sort"], ui["desc"] = key, False
        renpy.restart_interaction()

    def rcheats_sort_reset():
        rcheats_state.ui["sort"] = "order"
        rcheats_state.ui["desc"] = False
        renpy.restart_interaction()

    def rcheats_arrow(key):
        ui = rcheats_state.ui
        return (" v" if ui["desc"] else " ^") if ui["sort"] == key else ""

    def rcheats_page_move(delta, pages):
        ui = rcheats_state.ui
        ui["page"] = max(0, min(pages - 1, ui["page"] + delta))
        ui["edits"].clear()
        renpy.restart_interaction()

    def rcheats_cycle_page_size():
        ui = rcheats_state.ui
        sizes = [25, 50, 100, 250]
        ui["page_size"] = sizes[(sizes.index(ui["page_size"]) + 1) % len(sizes)] if ui["page_size"] in sizes else 50
        ui["page"] = 0
        renpy.restart_interaction()

    def rcheats_rebase():
        rcheats_mark_base()
        renpy.restart_interaction()

    # Inline editors. Typing commits as soon as the text is valid for the value's type, so there is no
    # separate Set step; Enter just drops the typing buffer.

    class RCheatsSearchInput(InputValue, FieldEquality):
        equality_fields = []
        identity_fields = []

        def __init__(self):
            self.default = True
            self.returnable = False

        def get_text(self):
            return rcheats_state.ui["query"]

        def set_text(self, s):
            rcheats_state.ui["query"] = s
            rcheats_state.ui["page"] = 0
            renpy.restart_interaction()

        def enter(self):
            renpy.restart_interaction()

    class RCheatsValueInput(InputValue, FieldEquality):
        equality_fields = ["key"]
        identity_fields = []

        def __init__(self, key, path):
            self.key = key
            self.path = path
            self.default = False
            self.returnable = False

        def get_text(self):
            edits = rcheats_state.ui["edits"]
            if self.key in edits:
                return edits[self.key]
            v = rcheats_read(self.path)
            if v is rcheats_missing:
                return ""
            return v if isinstance(v, rcheats_text_types) else rcheats_fmt(v)

        def set_text(self, s):
            rcheats_state.ui["edits"][self.key] = s
            rcheats_commit(self.path, s, True)
            renpy.restart_interaction()

        def enter(self):
            rcheats_state.ui["edits"].pop(self.key, None)
            renpy.restart_interaction()

    # Notifications and the change log

    def rcheats_passes_filter(label):
        terms = [t.strip().lower() for t in (persistent.rcheats_filter or "").split(",") if t.strip()]
        text = label.lower()
        include = [t for t in terms if not t.startswith("-")]
        exclude = [t[1:].strip() for t in terms if t.startswith("-") and len(t) > 1]
        if any(t in text for t in exclude):
            return False
        return not include or any(t in text for t in include)

    def rcheats_note(markup, label=None, force=False):
        if not force and (not persistent.rcheats_notify or (label is not None and not rcheats_passes_filter(label))):
            return
        now = rcheats_time.time()
        rcheats_state.notes.append((markup, now, now + float(persistent.rcheats_notify_secs or 4)))
        del rcheats_state.notes[:-6]

    def rcheats_note_test():
        rcheats_note("{color=#5fd35f}" + rcheats_zw + "money: 100 -> 120 (+20){/color}", force=True)
        rcheats_note("{color=#ff6b6b}" + rcheats_zw + "trust: 5 -> 3 (-2){/color}", force=True)
        rcheats_note(rcheats_zw + "flirting: False -> True", force=True)
        renpy.restart_interaction()

    # A note lives a little past its time so the slide-out can finish.
    def rcheats_live_notes():
        now = rcheats_time.time()
        return [n for n in rcheats_state.notes if now < n[2] + 0.35]

    def rcheats_tick():
        now = rcheats_time.time()
        kept = [n for n in rcheats_state.notes if now < n[2] + 0.35]
        if len(kept) != len(rcheats_state.notes):
            rcheats_state.notes[:] = kept
            renpy.restart_interaction()

    def rcheats_slide(born, dies, right, width):
        def fn(trans, st, at):
            now = rcheats_time.time()
            wait = None
            if now < born + 0.25:
                k = (1.0 - (now - born) / 0.25) ** 3
                wait = 0
            elif now > dies:
                k = min(1.0, (now - dies) / 0.3) ** 3
                wait = 0 if k < 1.0 else None
            else:
                k = 0.0
                wait = dies - now
            trans.xoffset = int((width + 40) * k) * (1 if right else -1)
            trans.alpha = 1.0 - k
            return wait
        return Transform(function=fn)

    def rcheats_report(label, old, new):
        if isinstance(new, rcheats_num_types) and not isinstance(new, bool) and isinstance(old, rcheats_num_types):
            delta = new - old
            col = "#5fd35f" if delta > 0 else "#ff6b6b"
            plain = "%s: %s -> %s (%s%s)" % (label, rcheats_fmt(old), rcheats_fmt(new), "+" if delta > 0 else "", rcheats_fmt(delta))
            markup = "{color=%s}%s%s{/color}" % (col, rcheats_zw, rcheats_esc(plain))
        else:
            plain = "%s: %s -> %s" % (label, rcheats_fmt(old), rcheats_fmt(new))
            markup = rcheats_zw + rcheats_esc(plain)
        rcheats_state.log.append((rcheats_time.strftime("%H:%M:%S"), markup))
        del rcheats_state.log[:-200]
        rcheats_note(markup, label)

    def rcheats_on_interact():
        try:
            rcheats_apply_pins(True)
            snap = {}
            for label, path, v in rcheats_entries():
                snap[rcheats_key(path)] = (label, v)
            old = rcheats_state.last
            rcheats_state.last = snap
            if old is None:
                return
            for key, item in snap.items():
                o = old.get(key)
                if o is None or (o[1] == item[1] and type(o[1]) == type(item[1])):
                    continue
                rcheats_report(item[0], o[1], item[1])
        except Exception:
            pass

    def rcheats_clear_log():
        del rcheats_state.log[:]
        renpy.restart_interaction()

    rcheats_state.on_open.append(rcheats_mark_base)

    try:
        config.interact_callbacks.append(rcheats_on_interact)
        config.python_callbacks.append(rcheats_apply_pins)
    except:
        pass

    try:
        config.overlay_screens.append("rcheats_notify")
    except:
        pass

screen rcheats_notify():
    zorder 150
    $ rc_notes = rcheats_live_notes()
    if rc_notes:
        timer 0.1 repeat True action Function(rcheats_tick)
        $ rc_corner = persistent.rcheats_corner
        $ rc_right = "r" in rc_corner
        $ rc_w = int(persistent.rcheats_notify_width)
        vbox:
            xalign (1.0 if rc_right else 0.0)
            yalign (0.0 if "t" in rc_corner else 1.0)
            xoffset (-16 if rc_right else 16)
            yoffset (16 if "t" in rc_corner else -110)
            spacing 6
            for rc_markup, rc_born, rc_dies in rc_notes:
                frame:
                    style "rcheats_note"
                    xsize rc_w
                    at rcheats_slide(rc_born, rc_dies, rc_right, rc_w)
                    text rc_markup style "rcheats_note_text" size int(persistent.rcheats_notify_size)

screen rcheats_tab_values():
    $ rc_ui = rcheats_state.ui
    $ rc_v = rcheats_view()
    $ rc_w = config.screen_width - 108
    $ rc_c = (int(rc_w * 0.13), int(rc_w * 0.28), int(rc_w * 0.07), int(rc_w * 0.27), int(rc_w * 0.11), int(rc_w * 0.09))
    vbox:
        spacing 8
        hbox:
            spacing 8
            $ rc_search = RCheatsSearchInput()
            button:
                style "rcheats_field"
                xsize 240
                action Function(rcheats_focus, rc_search)
                input value rc_search style "rcheats_input"
            for rc_id, rc_name in (("all", "All"), ("int", "Integers"), ("float", "Decimals"), ("bool", "Flags"), ("text", "Text")):
                textbutton rc_name:
                    style "rcheats_chip"
                    text_style "rcheats_chip_text"
                    action Function(rcheats_ui_set, "type", rc_id)
                    selected (rc_ui["type"] == rc_id)
                    yalign 0.5
            textbutton "Pinned only":
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_ui_toggle, "only_pins")
                selected rc_ui["only_pins"]
                yalign 0.5
            textbutton "Engine variables":
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_ui_toggle, "show_all")
                selected rc_ui["show_all"]
                yalign 0.5
        hbox:
            spacing 8
            text "Since you opened the menu:" style "rcheats_dim" yalign 0.5
            for rc_id, rc_name in (("all", "All"), ("chg", "Changed (%d)" % rc_v["changed"]), ("up", "Increased (%d)" % rc_v["up"]), ("down", "Decreased (%d)" % rc_v["down"])):
                textbutton rc_name:
                    style "rcheats_chip"
                    text_style "rcheats_chip_text"
                    action Function(rcheats_ui_set, "change", rc_id)
                    selected (rc_ui["change"] == rc_id)
                    yalign 0.5
            textbutton "Reset baseline":
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_rebase)
                yalign 0.5
            textbutton "Unpin all":
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_clear_pins)
                yalign 0.5
        hbox:
            spacing 8
            text ("%d values" % len(rc_v["rows"])) style "rcheats_dim" yalign 0.5
            if rc_ui["sort"] != "order":
                textbutton "Reset order":
                    style "rcheats_chip"
                    text_style "rcheats_chip_text"
                    action Function(rcheats_sort_reset)
            null width 20
            textbutton "Previous":
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_page_move, -1, rc_v["pages"])
                sensitive (rc_v["page"] > 0)
            text ("Page %d of %d" % (rc_v["page"] + 1, rc_v["pages"])) style "rcheats_dim" yalign 0.5
            textbutton "Next":
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_page_move, 1, rc_v["pages"])
                sensitive (rc_v["page"] + 1 < rc_v["pages"])
            textbutton ("%d rows" % rc_ui["page_size"]):
                style "rcheats_chip"
                text_style "rcheats_chip_text"
                action Function(rcheats_cycle_page_size)
        hbox:
            spacing 6
            fixed:
                xsize rc_c[0]
                ysize 26
                text "Section" style "rcheats_dim"
            textbutton ("Name" + rcheats_arrow("name")) style "rcheats_btn" text_style "rcheats_chip_text" action Function(rcheats_sort, "name") xsize rc_c[1] padding (0, 0) background None
            textbutton ("Type" + rcheats_arrow("type")) style "rcheats_btn" text_style "rcheats_chip_text" action Function(rcheats_sort, "type") xsize rc_c[2] padding (0, 0) background None
            textbutton ("Value" + rcheats_arrow("value")) style "rcheats_btn" text_style "rcheats_chip_text" action Function(rcheats_sort, "value") xsize rc_c[3] padding (0, 0) background None
            textbutton ("Change" + rcheats_arrow("change")) style "rcheats_btn" text_style "rcheats_chip_text" action Function(rcheats_sort, "change") xsize rc_c[4] padding (0, 0) background None
        viewport:
            scrollbars "vertical"
            vscrollbar_xsize 10
            vscrollbar_top_bar Solid("#1b2230")
            vscrollbar_bottom_bar Solid("#1b2230")
            vscrollbar_thumb Solid("#4a5a78")
            mousewheel True
            xfill True
            ysize config.screen_height - 290
            vbox:
                spacing 1
                for rc_n, rc_row in enumerate(rc_v["page_rows"]):
                    frame:
                        background ("#141922" if rc_n % 2 else "#10141b")
                        padding (4, 3)
                        xfill True
                        has hbox
                        spacing 6
                        fixed:
                            xsize rc_c[0]
                            ysize 26
                            text rcheats_esc(rcheats_clip(rc_row[3], rc_c[0] // 7)) style "rcheats_dim" yalign 0.5
                        fixed:
                            xsize rc_c[1]
                            ysize 26
                            text rcheats_esc(rcheats_clip(rc_row[4], rc_c[1] // 7)) style "rcheats_text" yalign 0.5
                        fixed:
                            xsize rc_c[2]
                            ysize 26
                            text rc_row[5] style "rcheats_dim" yalign 0.5
                        if rc_row[5] == "bool":
                            fixed:
                                xsize rc_c[3]
                                ysize 26
                                textbutton ("True" if rc_row[2] else "False"):
                                    style "rcheats_chip"
                                    text_style "rcheats_chip_text"
                                    text_color ("#5fd35f" if rc_row[2] else "#ff6b6b")
                                    action Function(rcheats_toggle_bool, rc_row[1])
                                    xminimum 60
                        else:
                            $ rc_in = RCheatsValueInput(rc_row[10], rc_row[1])
                            button:
                                style "rcheats_field"
                                xsize rc_c[3]
                                action Function(rcheats_focus, rc_in)
                                input value rc_in style "rcheats_input"
                        fixed:
                            xsize rc_c[4]
                            ysize 26
                            text rc_row[7] size 14 yalign 0.5 color ("#5fd35f" if rc_row[6] == "up" else ("#ff6b6b" if rc_row[6] == "down" else "#ffd54a"))
                        textbutton ("Pinned" if rc_row[8] else "Pin"):
                            style "rcheats_chip"
                            text_style "rcheats_chip_text"
                            action Function(rcheats_toggle_pin, rc_row[1])
                            selected rc_row[8]
                            xsize rc_c[5]
                if not rc_v["rows"]:
                    text "No values match these filters." style "rcheats_dim"

screen rcheats_tab_notify():
    vbox:
        spacing 12
        frame:
            style "rcheats_card"
            xfill True
            has vbox
            spacing 10
            text "Notifications" style "rcheats_heading"
            use rcheats_toggle("Show a notification when a value changes", "rcheats_notify")
            hbox:
                spacing 6
                text "Corner" style "rcheats_text" xminimum 150 yalign 0.5
                for rc_id, rc_name in (("tl", "Top left"), ("tr", "Top right"), ("bl", "Bottom left"), ("br", "Bottom right")):
                    textbutton rc_name:
                        style "rcheats_chip"
                        text_style "rcheats_chip_text"
                        action SetField(persistent, "rcheats_corner", rc_id)
                        selected (persistent.rcheats_corner == rc_id)
            use rcheats_slider("Text size", "rcheats_notify_size", 10, 32, "%d" % int(persistent.rcheats_notify_size))
            use rcheats_slider("Box width", "rcheats_notify_width", 180, 640, "%d" % int(persistent.rcheats_notify_width))
            use rcheats_slider("Time on screen", "rcheats_notify_secs", 1, 10, "%d s" % int(persistent.rcheats_notify_secs))
            hbox:
                spacing 12
                text "Only show names containing" style "rcheats_text" xminimum 150 yalign 0.5
                $ rc_fi = FieldInputValue(persistent, "rcheats_filter")
                button:
                    style "rcheats_field"
                    xsize 420
                    action Function(rcheats_focus, rc_fi)
                    input value rc_fi style "rcheats_input"
            text "Comma separated. Start a word with - to hide it, for example: money, trust, -timer" style "rcheats_dim"
            textbutton "Show a test notification":
                style "rcheats_btn"
                action Function(rcheats_note_test)

screen rcheats_tab_log():
    vbox:
        spacing 8
        textbutton "Clear log":
            style "rcheats_btn"
            action Function(rcheats_clear_log)
        viewport:
            scrollbars "vertical"
            vscrollbar_xsize 10
            vscrollbar_top_bar Solid("#1b2230")
            vscrollbar_bottom_bar Solid("#1b2230")
            vscrollbar_thumb Solid("#4a5a78")
            mousewheel True
            xfill True
            ysize config.screen_height - 200
            vbox:
                spacing 2
                for rc_time, rc_text in reversed(rcheats_state.log):
                    hbox:
                        spacing 12
                        text rc_time style "rcheats_dim"
                        text rc_text style "rcheats_text"
                if not rcheats_state.log:
                    text "No changes recorded yet. Changes are logged while the game runs, including ones caused by rollback." style "rcheats_dim"
