### rCheats menu framework
# The button is an overlay screen, so the game's own quick menu is never touched. It shows when the
# say window does, and F8 opens the menu on games whose layout hides or covers the button.

init -1 python:
    # Names that exist before the game's own init code, so the value editor can hide engine variables.
    rcheats_baseline = set(renpy.store.__dict__.keys())

init 998 python:
    import re as rcheats_re
    import time as rcheats_time

    rcheats_text_types = (type(u""), type(""))
    rcheats_int_types = (type(1), type(2 ** 70))
    rcheats_num_types = (float,) + rcheats_int_types

    # Marks the mod's own notification text so the text replacer leaves it alone.
    rcheats_zw = u"​"

    # Mutated in place and never reassigned, so it is not saved and not rolled back.
    class RCheatsState(object):
        def __init__(self):
            self.notes = []
            self.log = []
            self.last = None
            self.base = None
            self.walk_cache = {}
            self.hud_orig = None
            self.hud_done = False
            self.grow_orig = {}
            self.grow_done = False
            self.on_open = []
            self.ui = {"query": "", "type": "all", "change": "all", "show_all": False, "only_pins": False,
                       "sort": "order", "desc": False, "page": 0, "page_size": 50, "edits": {}}

    rcheats_state = RCheatsState()
    rcheats_missing = object()

    for _k, _v in (("rcheats_pins", {}), ("rcheats_notify", True), ("rcheats_corner", "tr"),
                   ("rcheats_filter", ""), ("rcheats_rules", []), ("rcheats_hud_alpha", 1.0),
                   ("rcheats_walk", True), ("rcheats_walk_fit", "default"), ("rcheats_walk_wrap", 40),
                   ("rcheats_notify_size", 16), ("rcheats_notify_width", 320),
                   ("rcheats_notify_secs", 4)):
        if getattr(persistent, _k) is None:
            setattr(persistent, _k, _v)

    # Titles shown in the tab bar, in the same order as the branches in screen rcheats.
    rcheats_tabs = [
        # {{TABS}}
    ]

    def rcheats_esc(s):
        return ("%s" % (s,)).replace("[", "[[").replace("{", "{{")

    def rcheats_fmt(v):
        if isinstance(v, float):
            s = ("%.4f" % v).rstrip("0").rstrip(".")
            return s or "0"
        return "%s" % (v,)

    def rcheats_noop(*args):
        pass

    # An empty input is only a few pixels wide, so the padded box around it focuses it on click.
    def rcheats_focus(value):
        renpy.set_editable_input_value(value, True)
        renpy.restart_interaction()

    def rcheats_open():
        if not renpy.get_screen("rcheats"):
            rcheats_state.ui["edits"].clear()
            for hook in rcheats_state.on_open:
                try:
                    hook()
                except Exception:
                    pass
            renpy.call_in_new_context("rcheats_entry")

    def rcheats_close():
        renpy.save_persistent()

    def rcheats_button_visible():
        return getattr(renpy.store, "quick_menu", True) and renpy.get_screen("say") is not None and not renpy.get_screen("rcheats")

    try:
        config.overlay_screens.append("rcheats_button")
        config.underlay.append(renpy.Keymap(rcheats=Function(rcheats_open)))
        config.keymap["rcheats"] = ["K_F8"]
    except:
        pass

# Styles. They inherit from "default" only, so a game's own button, input and bar styles never leak in.
style rcheats_text is default:
    size 15
    color "#d7dce5"

style rcheats_dim is rcheats_text:
    size 13
    color "#8a93a6"

style rcheats_heading is rcheats_text:
    size 18
    color "#ffffff"

style rcheats_panel is default:
    background "#0f1218"
    padding (22, 16)

style rcheats_card is default:
    background "#161b24"
    padding (14, 12)

style rcheats_field is default:
    background "#0b0e13"
    padding (8, 4)

style rcheats_input is rcheats_text:
    color "#ffffff"

style rcheats_btn is default:
    background "#232b3a"
    hover_background "#2f3b50"
    insensitive_background "#1a202b"
    padding (10, 4)

style rcheats_btn_text is rcheats_text:
    size 14
    hover_color "#ffffff"
    insensitive_color "#5d6678"

style rcheats_tab is default:
    background None
    hover_background "#1b2230"
    selected_background "#222c3d"
    padding (14, 8)

style rcheats_tab_text is rcheats_text:
    size 15
    hover_color "#ffffff"
    selected_color "#6cb6ff"

style rcheats_chip is default:
    background "#1b2230"
    hover_background "#2a3548"
    selected_background "#2c5d94"
    padding (9, 3)

style rcheats_chip_text is rcheats_text:
    size 13
    hover_color "#ffffff"
    selected_color "#ffffff"

style rcheats_bar is default:
    ysize 18
    left_bar Solid("#6cb6ff")
    right_bar Solid("#2a3340")
    thumb Solid("#ffffff", xsize=10, ysize=18)

style rcheats_note is default:
    background "#10141bf0"
    padding (12, 8)

style rcheats_note_text is rcheats_text:
    color "#ffffff"

label rcheats_entry:
    call screen rcheats
    return

screen rcheats_button():
    zorder 100
    if rcheats_button_visible():
        textbutton "rCheats":
            style "rcheats_chip"
            text_style "rcheats_chip_text"
            action Function(rcheats_open)
            xalign 1.0
            yalign 1.0
            yoffset -60
            background "#000000b0"

screen rcheats(start_tab=0):
    modal True
    zorder 200
    tag menu
    default tab = start_tab
    key "game_menu" action [Function(rcheats_close), Return()]
    add Solid("#05070bd8")
    frame:
        style "rcheats_panel"
        xalign 0.5
        yalign 0.5
        xsize config.screen_width - 60
        ysize config.screen_height - 60
        has vbox
        spacing 10
        hbox:
            spacing 4
            text "rCheats" style "rcheats_heading" yalign 0.5 xminimum 110
            for i, title in enumerate(rcheats_tabs):
                textbutton title:
                    style "rcheats_tab"
                    text_style "rcheats_tab_text"
                    action SetScreenVariable("tab", i)
                    selected (i == tab)
            null width 30
            textbutton "Close":
                style "rcheats_btn"
                action [Function(rcheats_close), Return()]
                yalign 0.5
        add Solid("#222a38", ysize=1)
        # one branch per tab: 6.99 has no "use expression"
        # {{BRANCHES}}

screen rcheats_toggle(label, field):
    hbox:
        spacing 12
        textbutton ("On" if getattr(persistent, field) else "Off"):
            style "rcheats_chip"
            text_style "rcheats_chip_text"
            action ToggleField(persistent, field)
            selected getattr(persistent, field)
            xminimum 44
        text label style "rcheats_text" yalign 0.5

screen rcheats_slider(label, field, low, high, shown=None, changed=None):
    hbox:
        spacing 12
        text label style "rcheats_text" xminimum 150 yalign 0.5
        bar value FieldValue(persistent, field, high - low, offset=low) style "rcheats_bar" xsize 260 changed (changed or rcheats_noop)
        text (shown or rcheats_fmt(getattr(persistent, field))) style "rcheats_dim" yalign 0.5

screen rcheats_tab_general():
    vbox:
        spacing 12
        frame:
            style "rcheats_card"
            xfill True
            has vbox
            spacing 6
            text "About" style "rcheats_heading"
            text "Open this menu with the rCheats button above the quick menu, or press F8." style "rcheats_dim"
            text "Delete zz-riis-cheats.rpy from the game folder to remove everything." style "rcheats_dim"
        # {{SECTIONS}}
