### Dynamic walkthrough
# When a choice menu is shown, the current node is the Menu. Each choice's block is read from the script
# AST (Python statements, if branches, and jumps or calls followed a couple of levels) and simple
# "x += n", "x -= n", "x = x + n" and "x = n" assignments are listed under the caption: green for gains,
# red for losses. Conditions are ignored, so every branch of an if is counted.

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
        parts = []
        for name, kind, v in totals:
            if kind == "a":
                cur = rcheats_missing
                if rcheats_expr_re.match(name):
                    try:
                        cur = eval(name, renpy.store.__dict__)
                    except Exception:
                        pass
                if cur is rcheats_missing or isinstance(cur, bool) or not isinstance(cur, (float,) + rcheats_int_types):
                    parts.append("{color=#dddddd}%s = %s{/color}" % (rcheats_esc(name), rcheats_fmt(v)))
                    continue
                v = v - cur
            if v == 0:
                continue
            parts.append("{color=%s}%s%s %s{/color}" % ("#5fd35f" if v > 0 else "#ff6b6b", "+" if v > 0 else "", rcheats_fmt(v), rcheats_esc(name)))
        if not parts:
            return ""
        return "\n{size=-8}" + "   ".join(parts) + "{/size}"

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
        text "Lists the values a choice raises (green) or lowers (red) under its caption. It reads the game's script, so it only sees plain assignments such as money += 5, and it ignores conditions." style "rcheats_dim"
