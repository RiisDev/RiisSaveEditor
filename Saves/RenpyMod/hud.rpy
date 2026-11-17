### HUD adjuster: opacity of the dialogue box
# Wraps the background of style.window (the dialogue window in classic and gui games) in a Transform.
# It is applied on the first interaction because styles can only be rebuilt once the game is running.

init 998 python:
    def rcheats_hud_apply(alpha=None):
        try:
            if alpha is None:
                alpha = persistent.rcheats_hud_alpha
            if rcheats_state.hud_orig is None:
                rcheats_state.hud_orig = [style.window.background]
            orig = rcheats_state.hud_orig[0]
            if orig is None:
                return
            style.window.background = Transform(orig, alpha=alpha) if alpha < 0.999 else orig
            style.rebuild()
        except Exception:
            pass

    def rcheats_hud_once():
        if not rcheats_state.hud_done:
            rcheats_state.hud_done = True
            if persistent.rcheats_hud_alpha < 0.999:
                rcheats_hud_apply()

    def rcheats_hud_reset():
        persistent.rcheats_hud_alpha = 1.0
        rcheats_hud_apply(1.0)
        renpy.restart_interaction()

    try:
        config.interact_callbacks.append(rcheats_hud_once)
    except:
        pass

screen rcheats_sec_hud():
    frame:
        style "rcheats_card"
        xfill True
        has vbox
        spacing 8
        text "Dialogue box" style "rcheats_heading"
        use rcheats_slider("Opacity", "rcheats_hud_alpha", 0.0, 1.0, "%d%%" % int(round(persistent.rcheats_hud_alpha * 100)), rcheats_hud_apply)
        hbox:
            spacing 10
            textbutton "Reset":
                style "rcheats_btn"
                action Function(rcheats_hud_reset)
            text "Changes the dialogue window style, so it applies to every window drawn with it." style "rcheats_dim" yalign 0.5
