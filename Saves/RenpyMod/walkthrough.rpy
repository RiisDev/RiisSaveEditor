### Dynamic walkthrough
# When a choice menu is shown, the current node is the Menu. Each choice's block is read from the script
# AST (Python statements, if branches, and jumps or calls followed a couple of levels) and simple
# "x += n", "x -= n", "x = x + n" and "x = n" assignments are listed under the caption: green for gains,
# red for losses. Conditions are ignored, so every branch of an if is counted.
# Fit modes (General page): Original is the first version (one line, no fitting); Default is Wrap plus Shrink;
# Wrap breaks it at a character limit; Grow clears a fixed button height on the standard choice styles so
# buttons grow with the text; Shrink lowers the text size as the number of effects grows; All is the three.

init 998 python:
    rcheats_assign_re = rcheats_re.compile(r"^([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[^\]\[]+\])*)\s*(\+=|-=|=)\s*(.+?)\s*$")
    rcheats_num_re = rcheats_re.compile(r"^[+-]?\d+(?:\.\d+)?$")
    rcheats_expr_re = rcheats_re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[^\]\[]+\])*$")

    def rcheats_parse_python(source, out):
        for line in source.split("\n"):
            line = line.split("#")[0].strip()
            m = rcheats_assign_re.match(line)
            if not m:
                continue
            name, op, rhs = m.group(1), m.group(2), m.group(3)
            if op == "+=" and rcheats_num_re.match(rhs):
                out.append((name, "d", float(rhs)))
            elif op == "-=" and rcheats_num_re.match(rhs):
                out.append((name, "d", -float(rhs)))
            elif op == "=":
                m2 = rcheats_re.match(r"^" + rcheats_re.escape(name) + r"\s*([+-])\s*(\d+(?:\.\d+)?)$", rhs)
                if m2:
                    out.append((name, "d", float(m2.group(2)) * (1 if m2.group(1) == "+" else -1)))
                elif rcheats_num_re.match(rhs):
                    out.append((name, "a", float(rhs)))

    def rcheats_collect(block, out, depth):
        for n in block:
            rcheats_visit(n, out, depth)

    def rcheats_visit(n, out, depth):
        if isinstance(n, renpy.ast.Python):
            rcheats_parse_python(n.code.source, out)
        elif isinstance(n, renpy.ast.If):
            for entry in n.entries:
                rcheats_collect(entry[1], out, depth)
        elif isinstance(n, (renpy.ast.Jump, renpy.ast.Call)) and depth < 2:
            if n.expression:
                return
            label = n.target if isinstance(n, renpy.ast.Jump) else n.label
            try:
                cur = renpy.game.script.lookup(label)
            except Exception:
                return
            steps = 0
            while cur is not None and steps < 80:
                if isinstance(cur, (renpy.ast.Menu, renpy.ast.Return)):
                    break
                rcheats_visit(cur, out, depth + 1)
                if isinstance(cur, renpy.ast.Jump):
                    break
                cur = cur.next
                steps += 1

    def rcheats_current_menu():
        try:
            n = renpy.game.script.lookup(renpy.game.context().current)
            if isinstance(n, renpy.ast.Menu):
                return n
        except Exception:
            pass
        return None

    def rcheats_walk_effects(node, text):
        key = (node.name, text)
        if key not in rcheats_state.walk_cache:
            out = []
            for label, cond, block in node.items:
                if block and label == text:
                    rcheats_collect(block, out, 0)
                    break
            rcheats_state.walk_cache[key] = out
        return rcheats_state.walk_cache[key]

    def rcheats_walk_suffix(text):
        if not persistent.rcheats_walk:
            return ""
        node = rcheats_current_menu()
        if node is None:
            return ""
        totals = []
        index = {}
        for name, kind, v in rcheats_walk_effects(node, text):
            if (name, kind) in index:
                if kind == "d":
                    totals[index[(name, kind)]][2] += v
                else:
                    totals[index[(name, kind)]][2] = v
            else:
                index[(name, kind)] = len(totals)
                totals.append([name, kind, v])
        parts = []  # (markup, length of the visible text)
        for name, kind, v in totals:
            if kind == "a":
                cur = rcheats_missing
                if rcheats_expr_re.match(name):
                    try:
                        cur = eval(name, renpy.store.__dict__)
                    except Exception:
                        pass
                if cur is rcheats_missing or isinstance(cur, bool) or not isinstance(cur, (float,) + rcheats_int_types):
                    parts.append(("{color=#dddddd}%s = %s{/color}" % (rcheats_esc(name), rcheats_fmt(v)), len(name) + len(rcheats_fmt(v)) + 3))
                    continue
                v = v - cur
            if v == 0:
                continue
            sign = "+" if v > 0 else ""
            parts.append(("{color=%s}%s%s %s{/color}" % ("#5fd35f" if v > 0 else "#ff6b6b", sign, rcheats_fmt(v), rcheats_esc(name)), len(sign) + len(rcheats_fmt(v)) + 1 + len(name)))
        if not parts:
            return ""
        return rcheats_walk_layout(parts, persistent.rcheats_walk_fit, int(persistent.rcheats_walk_wrap))

    # Pure layout: no engine calls, so it can be checked on its own.
    def rcheats_walk_layout(parts, fit, limit):
        # "original" is the first version: one line at a fixed size, none of the fit modes.
        wrap = fit in ("default", "wrap", "all")
        shrink = fit in ("default", "shrink", "all")
        lines = [[]]
        used = 0
        for markup, length in parts:
            if wrap and lines[-1] and used + 3 + length > limit:
                lines.append([])
                used = 0
            if lines[-1]:
                used += 3
            lines[-1].append(markup)
            used += length
        size = -8
        if shrink:
            size = -8 if len(parts) <= 3 else (-10 if len(parts) <= 6 else -12)
        return "\n{size=%d}" % size + "\n".join("   ".join(line) for line in lines) + "{/size}"

    # Grow: choice buttons often have a fixed height (ysize is shorthand for equal minimum and maximum
    # heights), which clips the extra line. Clear the maximum on the standard choice styles (gui games use
    # choice_button, classic layouts menu_choice_button) so the button can grow, and restore it when the mode
    # is left. Styles can only be rebuilt once the game is running, so this also runs on the first interaction.
    def rcheats_grow_apply():
        try:
            on = persistent.rcheats_walk and persistent.rcheats_walk_fit in ("grow", "all")
            saved = rcheats_state.grow_orig
            if not on and not saved:
                return
            for name in ("choice_button", "menu_choice_button"):
                try:
                    st = getattr(style, name)
                except Exception:
                    continue
                if on:
                    if name not in saved:
                        saved[name] = st.ymaximum
                    st.ymaximum = None
                elif name in saved:
                    st.ymaximum = saved[name]
            if not on:
                saved.clear()
            style.rebuild()
        except Exception:
            pass

    def rcheats_grow_once():
        if not rcheats_state.grow_done:
            rcheats_state.grow_done = True
            rcheats_grow_apply()

    try:
        config.interact_callbacks.append(rcheats_grow_once)
    except:
        pass

    def rcheats_install_walk():
        previous = config.say_menu_text_filter

        def rcheats_walk_filter(text):
            suffix = ""
            try:
                suffix = rcheats_walk_suffix(text)
            except Exception:
                pass
            base = previous(text) if previous is not None else text
            return base + suffix if suffix else base

        config.say_menu_text_filter = rcheats_walk_filter

    try:
        rcheats_install_walk()
    except:
        pass

screen rcheats_sec_walk():
    frame:
        style "rcheats_card"
        xfill True
        has vbox
        spacing 8
        text "Choice walkthrough" style "rcheats_heading"
        use rcheats_toggle("Show what each choice changes", "rcheats_walk")
        hbox:
            spacing 6
            text "Fit to the button" style "rcheats_text" xminimum 150 yalign 0.5
            for rc_id, rc_name in (("original", "Original"), ("default", "Default"), ("wrap", "Wrap"), ("grow", "Grow"), ("shrink", "Shrink"), ("all", "All")):
                textbutton rc_name:
                    style "rcheats_chip"
                    text_style "rcheats_chip_text"
                    action [SetField(persistent, "rcheats_walk_fit", rc_id), Function(rcheats_grow_apply)]
                    selected (persistent.rcheats_walk_fit == rc_id)
        use rcheats_slider("Wrap width", "rcheats_walk_wrap", 16, 80, "%d characters" % int(persistent.rcheats_walk_wrap))
        text "Original: the first version, one line and no fitting. Default: Wrap and Shrink. Wrap: break it at the wrap width. Grow: let choice buttons grow taller instead of clipping. Shrink: smaller text when a choice changes many values. All: all three. Games with a custom choice screen may ignore Grow." style "rcheats_dim"
        text "Lists the values a choice raises (green) or lowers (red) under its caption. It reads the game's script, so it only sees plain assignments such as money += 5, and it ignores conditions." style "rcheats_dim"
