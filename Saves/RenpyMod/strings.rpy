### String manipulator (name changer / relation editor)
# config.replace_text runs on every text token after tags are parsed, so tags are never touched and
# names, dialogue and menus are all covered. The menu itself is skipped so rules cannot break its labels.

init 998 python:
    def rcheats_replace_text(s):
        rules = persistent.rcheats_rules
        if not rules or s[:1] == rcheats_zw or renpy.get_screen("rcheats"):
            return s
        for r in rules:
            find = r.get("find")
            if not find:
                continue
            rep = r.get("rep") or ""
            flags = rcheats_re.IGNORECASE if r.get("ic", True) else 0
            s = rcheats_re.sub(rcheats_re.escape(find), lambda m: rep, s, flags=flags)
        return s

    def rcheats_add_rule():
        persistent.rcheats_rules.append({"find": "", "rep": "", "ic": True})
        renpy.restart_interaction()

    def rcheats_del_rule(i):
        del persistent.rcheats_rules[i]
        renpy.restart_interaction()

    try:
        _rcheats_prev_replace = config.replace_text

        def rcheats_chained_replace(s):
            if _rcheats_prev_replace is not None:
                s = _rcheats_prev_replace(s)
            return rcheats_replace_text(s)

        config.replace_text = rcheats_chained_replace
    except:
        pass

screen rcheats_tab_strings():
    vbox:
        spacing 10
        frame:
            style "rcheats_card"
            xfill True
            has vbox
            spacing 6
            text "Text replacer" style "rcheats_heading"
            text "Replaces text everywhere it is shown. Examples: Money -> Bitcoin, Roommate -> Best Friend, Nicole -> Jenny. Takes effect on the next line of text." style "rcheats_dim"
            hbox:
                spacing 10
                textbutton "Add rule":
                    style "rcheats_btn"
                    action Function(rcheats_add_rule)
                text "Aa lit up means the match is case sensitive." style "rcheats_dim" yalign 0.5
        viewport:
            scrollbars "vertical"
            vscrollbar_xsize 10
            vscrollbar_top_bar Solid("#1b2230")
            vscrollbar_bottom_bar Solid("#1b2230")
            vscrollbar_thumb Solid("#4a5a78")
            mousewheel True
            xfill True
            ysize config.screen_height - 270
            vbox:
                spacing 6
                for rc_i, rc_rule in enumerate(persistent.rcheats_rules):
                    hbox:
                        spacing 8
                        $ rc_a = DictInputValue(rc_rule, "find", default=False)
                        button:
                            style "rcheats_field"
                            xsize 280
                            action Function(rcheats_focus, rc_a)
                            input value rc_a style "rcheats_input"
                        text "->" style "rcheats_dim" yalign 0.5
                        $ rc_b = DictInputValue(rc_rule, "rep", default=False)
                        button:
                            style "rcheats_field"
                            xsize 280
                            action Function(rcheats_focus, rc_b)
                            input value rc_b style "rcheats_input"
                        textbutton "Aa":
                            style "rcheats_chip"
                            text_style "rcheats_chip_text"
                            action ToggleDict(rc_rule, "ic")
                            selected (not rc_rule.get("ic", True))
                            yalign 0.5
                        textbutton "Delete":
                            style "rcheats_chip"
                            text_style "rcheats_chip_text"
                            text_color "#ff6b6b"
                            action Function(rcheats_del_rule, rc_i)
                            yalign 0.5
