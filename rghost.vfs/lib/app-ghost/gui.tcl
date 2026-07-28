#   Ratio Ghost - BitTorrent ratio modifying proxy
#   Copyright (C) 2006-2015 Yasmine@RatioGhost.com
#
#   This program is free software: you can redistribute it and/or modify
#   it under the terms of the GNU General Public License as published by
#   the Free Software Foundation, either version 3 of the License, or
#   (at your option) any later version.
#
#   This program is distributed in the hope that it will be useful,
#   but WITHOUT ANY WARRANTY; without even the implied warranty of
#   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
#   GNU General Public License for more details.
#
#   You should have received a copy of the GNU General Public License
#   along with this program.  If not, see <http://www.gnu.org/licenses/>.


package provide app-gui 1.0
package require tooltip
package require autoscroll


if {$::WINDOWS} {
package require Winico 0.6
}

proc CreateGui {} {
    global status
    set status ""

    # Apply modern theme
    if {$::WINDOWS} {
        catch {ttk::style theme use vista}
    } else {
        catch {ttk::style theme use clam}
    }

    # Custom styles
    ttk::style configure TNotebook -tabmargins {2 5 2 0}
    ttk::style configure TNotebook.Tab -padding {12 4}
    ttk::style configure TLabel -padding {2 2}

    option add *tearOff 0

    wm withdraw .
    update idletasks
    wm title . "Ratio Ghost"
    wm minsize . 700 450

    menu .menubar
    . configure -menu .menubar

    menu .menubar.file
    .menubar add cascade -menu .menubar.file -label File -underline 0
    if {$::WINDOWS} {.menubar.file add command -label "Hide" -underline 0 -command {Hide}}
    .menubar.file add command -label "Exit" -underline 1 -command {Close}
    .menubar.file add separator
    .menubar.file add command -label "Export Log..." -underline 0 -command {ExportLog}

    menu .menubar.help
    .menubar add cascade -menu .menubar.help -label Help -underline 0
    .menubar.help add command -label "Show Debugging Console" -underline 5 -command {console show}
    .menubar.help add separator
    .menubar.help add command -label "Usage Statistics" -underline 6 -command {show_stats}
    .menubar.help add command -label "Visit Website" -underline 6 -command {show_website}
    .menubar.help add command -label "About" -underline 0 -command {show_about}


    if {!$::MAC} {
        if {[info exists ::rg_dir]} {
            set logofn [file join $::rg_dir logo.png]
        } else {
            set logofn logo.png
        }
        set logo [image create photo -file $logofn]
        set cv [ttk::label .logo -image $logo -anchor center]
        grid $cv -sticky nsew -pady 10 -padx 30
        grid rowconfigure . 0 -weight 0
    }

    set nb [ttk::notebook .nb]

    set log [CreateLog .nb.log]
    $nb add $log -text "  Log  "

    set options [CreateOptions .nb.options]
    $nb add $options -text "  Options  "

    set torrents [CreateTorrentsTab .nb.torrents]
    $nb add $torrents -text "  Torrents  "


    .nb select .nb.log

    grid $nb -sticky nsew
    grid columnconfigure . all -weight 1
    grid rowconfigure . 1 -weight 1

    # Enhanced status bar with Pause/Resume toggle
    set statusbar [ttk::frame .statusbar -padding {8 4}]
    ttk::label $statusbar.indicator -text "\u25CF" -foreground [expr {$::paused ? "#FFA500" : "#4CAF50"}]
    ttk::label $statusbar.status -textvariable status -anchor w
    ttk::button $statusbar.pause -text [expr {$::paused ? "Resume" : "Pause"}] -command TogglePause
    grid $statusbar.indicator $statusbar.status $statusbar.pause -sticky ew -padx 4
    grid columnconfigure $statusbar 1 -weight 1
    grid $statusbar -sticky ew


    if {[info exists ::settings(geometry)]} {
        wm geometry . $::settings(geometry)
    }

    wm deiconify .


    bind . <ButtonPress> "focus %W"
    bind . <Destroy> Kill

    wm protocol . WM_DELETE_WINDOW {
        if {$::WINDOWS} {
            Hide
        } else {
            Close
        }
    }

    CreateTrayIcon
}



proc CreateTrayIcon {} {
    if {!$::WINDOWS} return
    global tray_menu icon

    dlog_main "CreateTrayIcon start"
    set icon [winico load TK]

    set tray_menu [menu .popup]
    $tray_menu add command -label [expr {$::paused ? "Resume Ratio Ghost" : "Pause Ratio Ghost"}] -command TogglePause -underline 0
    $tray_menu add command -label "Hide Ratio Ghost" -command Hide -underline 6
    $tray_menu add command -label "Exit" -command Close -underline 2

    winico taskbar add $icon -pos 0 -callback [list TrayCallback %m %x %y]
    winico taskbar modify $icon -text "Ratio Ghost - Running"
    dlog_main "CreateTrayIcon done icon=$icon"
}

proc NormalizeTrayMessage {msg} {
    switch -exact -- $msg {
        513 {return WM_LBUTTONDOWN}
        514 {return WM_LBUTTONUP}
        515 {return WM_LBUTTONDBLCLK}
        516 {return WM_RBUTTONDOWN}
        517 {return WM_RBUTTONUP}
        default {return $msg}
    }
}

proc ShowTrayMenu {x y} {
    global tray_menu
    dlog_main "ShowTrayMenu x=$x y=$y"
    if {![info exists tray_menu] || ![winfo exists $tray_menu]} return
    catch {$tray_menu unpost}
    catch {focus -force .}
    tk_popup $tray_menu $x $y
}

proc TrayRightClickFallback {x y token} {
    if {[info exists ::tray_right_click_token] && $::tray_right_click_token eq $token} {
        unset ::tray_right_click_token
        ShowTrayMenu $x $y
    }
}


proc ShowApp {} {
    global last_state tray_menu
    dlog_main "ShowApp state=[wm state .]"
    if {!$::WINDOWS} return
    if {[wm state .] eq "withdrawn"} {
        $tray_menu entryconfigure 1 -label "Hide Ratio Ghost" -underline 6
        if {[info exists last_state] && $last_state ne ""} {
            wm state . $last_state
        } else {
            wm state . normal
        }
        wm deiconify .
    }
    raise .
    focus -force .
}


proc TrayCallback {msg x y} {
    global tray_menu
    dlog_main "TrayCallback raw msg=$msg x=$x y=$y"
    if {[catch {
        set msg [NormalizeTrayMessage $msg]
        dlog_main "TrayCallback normalized msg=$msg"
        switch -exact -- $msg {
            WM_RBUTTONDOWN {
                set ::tray_right_click_token [clock clicks]
                after 250 [list TrayRightClickFallback $x $y $::tray_right_click_token]
            }
            WM_RBUTTONUP {
                catch {unset ::tray_right_click_token}
                ShowTrayMenu $x $y
            }
            WM_LBUTTONDOWN {
                ShowApp
            }
            WM_LBUTTONUP {
                ShowApp
            }
            WM_LBUTTONDBLCLK {
                ShowApp
            }
        }
    } err]} {
        catch {logerror "Tray callback error: $err\n\n$::errorInfo"}
    }
}


proc Hide {} {
    if {!$::WINDOWS} return

    global last_state tray_menu
    if {[wm state .] eq "withdrawn"} {
        $tray_menu entryconfigure 1 -label "Hide Ratio Ghost" -underline 6
        if {[info exists last_state] && $last_state ne ""} {
            wm state . $last_state
        } else {
            wm state . normal
        }
        wm deiconify .
        raise .
        focus -force .
    } else {
        $tray_menu entryconfigure 1 -label "Show Ratio Ghost" -underline 6
        set last_state [wm state .]
        wm withdraw .
    }
}


proc TogglePause {} {
    global tray_menu
    if {$::paused} {
        set ::paused 0
        catch {.statusbar.indicator configure -foreground "#4CAF50"}
        catch {.statusbar.pause configure -text "Pause"}
        if {[info exists tray_menu]} {
            catch {$tray_menu entryconfigure 0 -label "Pause Ratio Ghost"}
        }
        Event "Proxy resumed - ratio modifying active"
    } else {
        set ::paused 1
        catch {.statusbar.indicator configure -foreground "#FFA500"}
        catch {.statusbar.pause configure -text "Resume"}
        if {[info exists tray_menu]} {
            catch {$tray_menu entryconfigure 0 -label "Resume Ratio Ghost"}
        }
        Event "Proxy paused - passing through actual stats"
    }
    update_status
}


set ::log_filter ""

proc CreateLog {name} {
    global lb

    ttk::frame $name -padding 10

    # Search/filter bar
    set search_frame [ttk::frame $name.search]
    ttk::label $search_frame.lbl -text "Filter:"
    ttk::entry $search_frame.entry -textvariable ::log_filter -width 30
    ttk::button $search_frame.clear -text "Clear" -command {set ::log_filter ""; FilterLog}
    grid $search_frame.lbl $search_frame.entry $search_frame.clear -padx 4 -sticky w
    grid $search_frame -sticky ew -pady {0 8}

    set lb [text $name.l1 -state disabled -wrap none -yscrollcommand [list $name.scroll set] \
        -font {Consolas 9} -background "#1e1e1e" -foreground "#d4d4d4" \
        -selectbackground "#264f78" -insertbackground "#d4d4d4" \
        -relief flat -borderwidth 1 -padx 8 -pady 4]
    set scroll [ttk::scrollbar $name.scroll -orient vertical -command [list $lb yview]]
    grid $lb $scroll -sticky nsew -row 1

    grid columnconfigure $name 0 -weight 1
    grid rowconfigure $name 1 -weight 1

    ::autoscroll::autoscroll $scroll

    # Color tags for different event types
    $lb tag configure success -foreground "#4EC9B0"
    $lb tag configure error -foreground "#F44747"
    $lb tag configure warning -foreground "#CCA700"
    $lb tag configure info -foreground "#569CD6"
    $lb tag configure blocked -foreground "#808080"
    $lb tag configure timestamp -foreground "#6A9955"
    $lb tag configure highlight -background "#3a3d41"

    bind $lb <Double-ButtonPress-1> [list EventLogShow $lb %x %y]
    bind $search_frame.entry <KeyRelease> FilterLog

    return $name
}


set example {}

proc bindMouseWheel {w canvas} {
    bind $w <MouseWheel> "$canvas yview scroll \[expr {-%D/120}\] units"
    bind $w <Button-4> [list $canvas yview scroll -1 units]
    bind $w <Button-5> [list $canvas yview scroll 1 units]
    foreach child [winfo children $w] {
        bindMouseWheel $child $canvas
    }
}
proc CreateOptions {name} {
    ttk::frame $name

    set canvas [canvas $name.canvas -bd 0 -highlightthickness 0 -background [. cget -background]]
    set sb [ttk::scrollbar $name.sb -orient vertical -command [list $canvas yview]]
    $canvas configure -yscrollcommand [list $sb set]

    set p [ttk::frame $canvas.f -padding 20]
    $canvas create window 0 0 -anchor nw -window $p

    bind $p <Configure> [list apply [list {c} {
        $c configure -scrollregion [$c bbox all]
    }] $canvas]

    bind $canvas <Configure> [list apply [list {c f} {
        $c itemconfigure 1 -width [winfo width $c]
    }] $canvas $p]

    grid $canvas -row 0 -column 0 -sticky nsew
    grid $sb -row 0 -column 1 -sticky ns
    grid columnconfigure $name 0 -weight 1
    grid rowconfigure $name 0 -weight 1

    ::autoscroll::autoscroll $sb

    set warning [ttk::label $p.warn -foreground red -anchor center -text "It is highly recommended that you close your torrent client before changing any settings here."]

    set ratio [ttk::labelframe $p.ratio -text "Ratio Options" -padding 12]

    set lpeer [ttk::label $ratio.lpeer -text "If torrent has less than " -anchor e]
    set epeer [ttk::entry $ratio.epeer -textvariable ::settings(min_peers) -validate key -validatecommand {ValidatePer %P} -width 7]
    set lpeer2 [ttk::label $ratio.lpeer2 -text "leechers, then report only the actual upload amount" -anchor w]
    grid $lpeer $epeer $lpeer2 - - - -padx 4 -pady 4 -sticky ew



    grid [ttk::frame $ratio.space1] -pady 6

    set lother [ttk::label $ratio.lother -text "Otherwise, report the actual upload amount..." -anchor w]
    set lratd [ttk::label $ratio.lratd -text "plus between" -anchor e]
    set ratad [ttk::entry $ratio.ratad -textvariable ::settings(updown_ratio_a) -validate key -validatecommand {ValidateReal %P} -width 7]
    set landd [ttk::label $ratio.landd -text "and"]
    set ratbd [ttk::entry $ratio.ratbd -textvariable ::settings(updown_ratio_b) -validate key -validatecommand {ValidateReal %P} -width 7]
    set ltimed [ttk::label $ratio.ltimed -text "times actual download"]
    grid $lother - - - - -padx 4 -pady 4 -sticky ew
    grid $lratd $ratad $landd $ratbd $ltimed -padx 4 -pady 4 -sticky ew


    set lrat [ttk::label $ratio.lrat -text "plus between" -anchor e]
    set rata [ttk::entry $ratio.rata -textvariable ::settings(upup_ratio_a) -validate key -validatecommand {ValidateReal %P} -width 7]
    set land [ttk::label $ratio.land -text "and"]
    set ratb [ttk::entry $ratio.ratb -textvariable ::settings(upup_ratio_b) -validate key -validatecommand {ValidateReal %P} -width 7]
    set ltime [ttk::label $ratio.ltime -text "times actual upload"]
    grid $lrat $rata $land $ratb $ltime -padx 4 -pady 4 -sticky ew



    set lb1 [ttk::label $ratio.lb1 -text "plus up to" -anchor e]
    set lb [ttk::entry $ratio.lb -textvariable ::settings(boost) -validate key -validatecommand {ValidateReal %P} -width 7]
    set lpc [ttk::label $ratio.lpc -text "KB/s with"]
    set bp [ttk::entry $ratio.bp -textvariable ::settings(boost_chance) -validate key -validatecommand {ValidatePer %P} -width 7]
    set lkb [ttk::label $ratio.lkb -text "percent chance"]

    grid $lb1 $lb $lpc $bp $lkb  -padx 4 -pady 4 -sticky ew



    grid [ttk::frame $ratio.space2] -pady 6


    set chk_ndown [ttk::checkbutton $ratio.chk_ndown -text "Report download as zero" -variable ::settings(no_download)]
    tooltip::tooltip $chk_ndown "AKA FreeLeech. This will report the amount downloaded as zero.\nIt will also block the complete flag when your download finishes.\nYou will still get credit for your upload."
    grid x $chk_ndown - - - -padx 4 -pady 4 -sticky w

    set chk_seed [ttk::checkbutton $ratio.chk_seed -text "Pretend to seed" -variable ::settings(seed)]
    tooltip::tooltip $chk_seed "This will set the reported amount left as zero, making you appear as a seed.\nMany servers don't send peer lists to seeds - this can slow your download."
    grid x $chk_seed - - - -padx 4 -pady 4 -sticky w



    SetExample

    set example_frame [ttk::labelframe $ratio.exf -text "Example" -padding 12]
    grid $example_frame -row 1 -column 5 -rowspan 8

    set lexample [ttk::label $example_frame.lexample -textvariable ::example -anchor w]
    grid $lexample -sticky nsew


    grid columnconfigure $ratio 4 -weight 1


    set k [list apply [list {args} "
        if {\$::settings(seed)} {
            set ::settings(no_download) 1
            $chk_ndown configure -state disabled
        } else {
            $chk_ndown configure -state normal
        }
    "]]

    trace add variable ::settings(seed) write $k

    set ::settings(seed) $::settings(seed)


    trace add variable ::settings write SetExample

    set connection [ttk::labelframe $p.connection -text "Connection Options" -padding 12]

    set l3 [ttk::label $connection.l3 -text "Listen for incoming connections on port" -anchor e]
    set e3 [ttk::entry $connection.e3 -textvariable ::settings(listen_port) -validate key -validatecommand {ValidatePort %P} -width 7]
    tooltip::tooltip $e3 "What port Ratio Ghost listens on.\nLeave this set to 3773 unless you're using that port for something else."
    grid $l3 $e3 -padx 4 -pady 4 -sticky ew

    set chk_tracker [ttk::checkbutton $connection.chk_tracker -text "Accept only tracker traffic" -variable ::settings(only_tracker)]
    tooltip::tooltip $chk_tracker "This will block proxy traffic that doesn't appear to be torrent tracker related.\nThis option may break your torrent client's update feature, and it may block ads if your torrent client is ad supported."
    grid x $chk_tracker - - -padx 4 -pady 4 -sticky w

    set chk_local [ttk::checkbutton $connection.chk_local -text "Accept only local connections" -variable ::settings(only_local)]
    tooltip::tooltip $chk_local "This will block proxy traffic that isn't coming from your computer.\nLeave this checked for security unless you know what you're doing."
    grid x $chk_local - - -padx 4 -pady 4 -sticky w

    if {$::WINDOWS} {
        set chk_autostart [ttk::checkbutton $connection.chk_autostart -text "Start Ratio Ghost automatically when Windows boots" -variable ::settings(autostart)]
        tooltip::tooltip $chk_autostart "This will add Ratio Ghost to the Windows startup registry so it starts automatically in the tray when Windows boots."
        grid x $chk_autostart - - -padx 4 -pady 4 -sticky w

        set chk_minimized [ttk::checkbutton $connection.chk_minimized -text "Start minimized to system tray (in background)" -variable ::settings(start_minimized)]
        tooltip::tooltip $chk_minimized "This will start Ratio Ghost in the system tray when launched."
        grid x $chk_minimized - - -padx 4 -pady 4 -sticky w
    }

    set chk_update [ttk::checkbutton $connection.chk_update -text "Automatically check for software updates" -variable ::settings(update)]
    tooltip::tooltip $chk_update "New versions of Ratio Ghost are released occasionally that may add features or improve stealth.\nChecking this will notify you when an update is available."
    grid x $chk_update - - -padx 4 -pady 4 -sticky w



    grid $warning -sticky ew -pady 10
    grid $ratio -sticky ew -pady 10
    grid $connection -sticky ew -pady 10
    grid columnconfigure $p 0 -weight 1

    bindMouseWheel $p $canvas

    return $name
}


proc SetExample {args} {
    global example settings

    set example ""

    append example "If the torrent has less than $settings(min_peers) leechers:"
    append example "\nThe reported download will be"
    if {$settings(no_download)} {
        append example " 0."
    } else {
        append example " your actual download."
    }

    append example "\nThe reported upload will be your actual upload."


    append example "\n\nIf the torrent has at least $settings(min_peers) leechers:"
    append example "\nThe reported download will be"
    if {$settings(no_download)} {
        append example " 0."
    } else {
        append example " your actual download."
    }
    append example "\nThe reported upload will be your actual upload"
    append example "\nand between $settings(updown_ratio_a) and $settings(updown_ratio_b) times your actual download"
    append example "\nand between $settings(upup_ratio_a) and $settings(upup_ratio_b) times your actual upload"
    append example "\nand $settings(boost_chance) percent of the time an extra 0-$settings(boost) KB/s."
}


set ::sort_column ""
set ::sort_direction 0

proc CompareItems {tree col direction a b} {
    set valA [GetRawValue $a $col]
    set valB [GetRawValue $b $col]

    set numeric 1
    if {$col in {tracker status last_announce}} {
        set numeric 0
    }

    if {$numeric} {
        if {$valA < $valB} {
            return [expr {$direction ? 1 : -1}]
        } elseif {$valA > $valB} {
            return [expr {$direction ? -1 : 1}]
        } else {
            return 0
        }
    } else {
        set cmp [string compare -nocase $valA $valB]
        return [expr {$direction ? -$cmp : $cmp}]
    }
}

proc GetRawValue {hash col} {
    switch -exact -- $col {
        tracker {
            if {[info exists ::hash_tracker($hash)]} {
                return $::hash_tracker($hash)
            }
            return "unknown"
        }
        actual_down {
            if {[info exists ::actual_sum($hash)]} {
                return [lindex $::actual_sum($hash) 0]
            }
            return 0
        }
        actual_up {
            if {[info exists ::actual_sum($hash)]} {
                return [lindex $::actual_sum($hash) 1]
            }
            return 0
        }
        reported_down {
            if {[info exists ::reported_sum($hash)]} {
                return [lindex $::reported_sum($hash) 0]
            }
            return 0
        }
        reported_up {
            if {[info exists ::reported_sum($hash)]} {
                return [lindex $::reported_sum($hash) 1]
            }
            return 0
        }
        ratio {
            if {[info exists ::reported_sum($hash)]} {
                lassign $::reported_sum($hash) rd ru
                if {$rd == 0} {
                    if {$ru == 0} {return 0.0}
                    return 999999.0
                }
                return [expr {1.0 * $ru / $rd}]
            }
            return 0.0
        }
        seeds {
            if {[info exists ::response($hash,complete)]} {
                return $::response($hash,complete)
            }
            return 0
        }
        leechers {
            if {[info exists ::response($hash,incomplete)]} {
                return $::response($hash,incomplete)
            }
            return 0
        }
        status {
            if {[info exists ::paused] && $::paused} {
                return "Paused"
            }
            if {[info exists ::auto_seed_only] && $::auto_seed_only} {
                return "Seed-only standby"
            }
            if {[info exists ::response($hash,incomplete)] && $::response($hash,incomplete) < $::settings(min_peers)} {
                return "Waiting for leechers"
            }
            return "Ready"
        }
        last_announce {
            if {[info exists ::reported_last_time($hash)]} {
                return $::reported_last_time($hash)
            }
            return 0
        }
        default {
            return ""
        }
    }
}

proc SortTorrentsTab {tree col direction} {
    set ::sort_column $col
    set ::sort_direction $direction

    set items [$tree children {}]
    set sorted_items [lsort -command [list CompareItems $tree $col $direction] $items]

    set idx 0
    foreach item $sorted_items {
        $tree move $item {} $idx
        incr idx
    }

    foreach c [$tree cget -columns] {
        set text [$tree heading $c -text]
        regsub -all { \u25b2| \u25bc} $text {} text
        if {$c eq $col} {
            if {$direction == 0} {
                append text " \u25b2"
                $tree heading $c -text $text -command [list SortTorrentsTab $tree $c 1]
            } else {
                append text " \u25bc"
                $tree heading $c -text $text -command [list SortTorrentsTab $tree $c 0]
            }
        } else {
            $tree heading $c -text $text -command [list SortTorrentsTab $tree $c 0]
        }
    }
}

# Torrents tab - shows per-torrent statistics
proc CreateTorrentsTab {name} {
    ttk::frame $name -padding 10

    set tree [ttk::treeview $name.tree -columns {tracker seeds leechers status last_announce} -show headings \
        -yscrollcommand [list $name.scroll set]]
    set scroll [ttk::scrollbar $name.scroll -orient vertical -command [list $tree yview]]

    $tree heading tracker -text "Tracker"
    $tree heading seeds -text "Seeds"
    $tree heading leechers -text "Leechers"
    $tree heading status -text "Status"
    $tree heading last_announce -text "Last Announce"

    $tree column tracker -width 260 -minwidth 120
    $tree column seeds -width 60 -minwidth 40 -anchor center
    $tree column leechers -width 60 -minwidth 40 -anchor center
    $tree column status -width 150 -minwidth 110 -anchor center
    $tree column last_announce -width 100 -minwidth 80 -anchor center

    grid $tree $scroll -sticky nsew
    grid columnconfigure $name 0 -weight 1
    grid rowconfigure $name 0 -weight 1

    ::autoscroll::autoscroll $scroll

    set ::torrent_tree $tree

    # Configure sorting on headings
    foreach col [$tree cget -columns] {
        $tree heading $col -command [list SortTorrentsTab $tree $col 0]
    }

    # Bind right-click context menu
    bind $tree <ButtonPress-3> [list ShowTorrentContextMenu $tree %x %y %X %Y]
    if {$::MAC} {
        bind $tree <ButtonPress-2> [list ShowTorrentContextMenu $tree %x %y %X %Y]
        bind $tree <Control-ButtonPress-1> [list ShowTorrentContextMenu $tree %x %y %X %Y]
    }

    # Refresh every 3 seconds
    after 3000 UpdateTorrentsTab

    return $name
}

proc ShowTorrentContextMenu {tree x y X Y} {
    set item [$tree identify row $x $y]
    if {$item ne ""} {
        $tree selection set $item

        if {[info commands .torrent_context] eq ""} {
            menu .torrent_context -tearoff 0
            .torrent_context add command -label "Copy Info Hash" -command [list CopyTorrentHash $tree]
            .torrent_context add command -label "Reset Statistics" -command [list ResetTorrentStats $tree]
        }

        tk_popup .torrent_context $X $Y
    }
}

proc CopyTorrentHash {tree} {
    set sel [$tree selection]
    if {[llength $sel] == 0} return
    set hash [lindex $sel 0]
    clipboard clear
    clipboard append $hash
    Event "Copied info hash to clipboard: $hash"
}

proc ResetTorrentStats {tree} {
    set sel [$tree selection]
    if {[llength $sel] == 0} return
    set hash [lindex $sel 0]

    set r [tk_messageBox -title "Reset Statistics" -message "Are you sure you want to reset all tracked statistics for this torrent?" -type yesno -default no]
    if {$r eq "yes"} {
        catch {unset ::actual_first($hash)}
        catch {unset ::actual_last($hash)}
        catch {unset ::actual_sum($hash)}
        catch {unset ::reported_last($hash)}
        catch {unset ::reported_sum($hash)}
        catch {unset ::reported_last_time($hash)}
        catch {unset ::response($hash,complete)}
        catch {unset ::response($hash,incomplete)}
        catch {unset ::response($hash,interval)}

        $tree delete $hash
        Event "Reset stats for torrent hash: [string range $hash 0 7]..."
        update_status
    }
}

proc UpdateTorrentsTab {} {
    after 3000 UpdateTorrentsTab

    if {![info exists ::torrent_tree]} return
    set tree $::torrent_tree

    set existing_items {}
    foreach item [$tree children {}] {
        lappend existing_items $item
    }

    set active_hashes [array names ::actual_sum]

    foreach item $existing_items {
        if {$item ni $active_hashes} {
            $tree delete $item
        }
    }

    foreach hash $active_hashes {
        set seeds 0
        set leechers 0
        if {[info exists ::response($hash,complete)]} {
            set seeds $::response($hash,complete)
        }
        if {[info exists ::response($hash,incomplete)]} {
            set leechers $::response($hash,incomplete)
        }

        set torrent_status "Ready"
        if {[info exists ::paused] && $::paused} {
            set torrent_status "Paused"
        } elseif {[info exists ::auto_seed_only] && $::auto_seed_only} {
            set torrent_status "Seed-only standby"
        } elseif {$leechers < $::settings(min_peers)} {
            set torrent_status "Waiting for leechers"
        }

        set tracker "unknown"
        if {[info exists ::hash_tracker($hash)]} {
            set tracker $::hash_tracker($hash)
        }

        set last_time "-"
        if {[info exists ::reported_last_time($hash)]} {
            set last_time [clock format $::reported_last_time($hash) -format %H:%M:%S]
        }

        set values [list \
            $tracker \
            $seeds \
            $leechers \
            $torrent_status \
            $last_time]

        if {[$tree exists $hash]} {
            $tree item $hash -values $values
        } else {
            $tree insert {} end -id $hash -values $values
        }
    }

    if {[info exists ::sort_column] && $::sort_column ne ""} {
        SortTorrentsTab $tree $::sort_column $::sort_direction
    }
}


set EventIndexDel 0
set EventIndex 0
set EventFlushScheduled 0
set EventQueue {}

proc EventTag {what} {
    set tag "info"
    if {[string match "*down/up from*" $what]} {
        set tag "success"
    } elseif {[string match "*ERROR*" $what] || [string match "*error*" $what] || [string match "*fail*" $what]} {
        set tag "error"
    } elseif {[string match "*Blocked*" $what] || [string match "*blocked*" $what]} {
        set tag "blocked"
    } elseif {[string match "*Tunnel*" $what] || [string match "*Intercepting*" $what] || [string match "*timeout*" $what]} {
        set tag "warning"
    }
    return $tag
}

proc ScheduleEventFlush {} {
    if {$::EventFlushScheduled} return
    set ::EventFlushScheduled 1
    after 100 FlushEvents
}

proc FlushOneEvent {idx ts what tag} {
    global lb
    $lb configure -state normal
    $lb insert end "$ts " timestamp
    $lb insert end "$what\n" $tag
    if {[info exists ::event_append_pending($idx)]} {
        $lb insert "end - 1 chars" $::event_append_pending($idx)
        unset ::event_append_pending($idx)
    }
    $lb configure -state disabled

    # Clean up old entries (keep max 512 lines)
    set max_lines 512
    if {[expr {int([$lb index end])}] > $max_lines + 1} {
        incr ::EventIndexDel
        set del_idx [expr {$::EventIndexDel - 1}]
        if {[info exists ::log_lookup($del_idx)]} {
            set del_log $::log_lookup($del_idx)
            unset -nocomplain ::event_log($del_log)
            unset -nocomplain ::log_referenced($del_log)
            unset -nocomplain ::log_lookup($del_idx)
        }
        unset -nocomplain ::event_append_pending($del_idx)
        $lb configure -state normal
        $lb delete 1.0 2.0
        $lb configure -state disabled
    }

    return
}

proc FlushEvents {} {
    global lb
    set ::EventFlushScheduled 0

    if {![info exists lb] || ![winfo exists $lb]} {
        set ::EventQueue {}
        return
    }

    set processed 0
    set max_per_flush 40
    while {[llength $::EventQueue] > 0 && $processed < $max_per_flush} {
        set item [lindex $::EventQueue 0]
        set ::EventQueue [lrange $::EventQueue 1 end]
        lassign $item idx ts what tag
        FlushOneEvent $idx $ts $what $tag
        incr processed
    }

    catch {$lb see end}

    if {[llength $::EventQueue] > 0} {
        ScheduleEventFlush
    }
}

proc Event {what} {
    set ts [clock format [clock seconds] -format %I:%M%P]
    set tag [EventTag $what]
    set idx $::EventIndex
    incr ::EventIndex
    lappend ::EventQueue [list $idx $ts $what $tag]
    ScheduleEventFlush
    return $idx
}


proc EventAppend {idx what} {
    global lb

    if {$idx eq ""} {return}
    if {$idx < $::EventIndexDel} {return}

    set line_idx [expr {$idx - $::EventIndexDel + 1}]
    if {$line_idx < 1} {
        append ::event_append_pending($idx) $what
        return
    }

    if {![info exists lb] || ![winfo exists $lb]} {
        append ::event_append_pending($idx) $what
        return
    }
    if {$line_idx >= [expr {int([$lb index end])}]} {
        append ::event_append_pending($idx) $what
        return
    }

    $lb configure -state normal
    # Insert before the newline at the end of the line
    $lb insert "$line_idx.0 lineend" $what
    $lb configure -state disabled
    $lb see end
}



proc EventLogShow {window x y} {

    set click_idx [$window index @$x,$y]
    set line_num [lindex [split $click_idx .] 0]
    set idx [expr {$line_num - 1 + $::EventIndexDel}]

    if {![info exists ::log_lookup($idx)]} {
        return
    }
    if {![info exists ::event_log($::log_lookup($idx))]} {
        return
    }

    set n .eventlog$idx

    if {[info commands $n] ne {}} {
        destroy $n
    }

    set t [toplevel $n]
    wm title $t "Event $idx"

    #wm resizable $t 0 0

    set f [ttk::frame $t.f -padding 20]
    grid $f -sticky nsew

    set l [ttk::label $f.stats -text $::event_log($::log_lookup($idx))]
    grid $l

    focus $t
}


proc FilterLog {} {
    global lb
    $lb tag remove highlight 1.0 end
    set filter $::log_filter
    if {$filter eq ""} return

    set start 1.0
    while {1} {
        set pos [$lb search -nocase -- $filter $start end]
        if {$pos eq ""} break
        set line_start "[lindex [split $pos .] 0].0"
        set line_end "[lindex [split $pos .] 0].end"
        $lb tag add highlight $line_start $line_end
        set start "[lindex [split $pos .] 0].end"
    }
}


proc ExportLog {} {
    global lb
    set filename [tk_getSaveFile -title "Export Log" -defaultextension .txt \
        -filetypes {{"Text Files" .txt} {"All Files" *}}]
    if {$filename eq ""} return
    set f [open $filename w]
    puts -nonewline $f [$lb get 1.0 end]
    close $f
    Event "Log exported to $filename"
}


proc show_stats {} {

    if {[info commands .stats] ne {}} {
        destroy .stats
    }

    set t [toplevel .stats]
    wm title $t "RG Usage"

    wm resizable $t 0 0

    set f [ttk::frame $t.f -padding 20]
    grid $f -sticky nsew


    set stats ""

    append stats "First use on: [clock format $::settings(first)]\n"
    append stats "Total runtime: [FormatElapsed [expr {$::settings(runtime) + ([clock seconds] - $::last_save_time)}]]\n"
    append stats "Total sessions: $::settings(sessions)\n"

    append stats "\nActual total download: [FormatData [expr {$::settings(actual_down) + $::actual_down - $::saved_counter(actual_down)}]]\n"
    append stats "Actual total upload: [FormatData [expr {$::settings(actual_up) + $::actual_up - $::saved_counter(actual_up)}]]\n"

    append stats "\nReported total download: [FormatData [expr {$::settings(reported_down) + $::reported_down - $::saved_counter(reported_down)}]]\n"
    append stats "Reported total upload: [FormatData [expr {$::settings(reported_up) + $::reported_up - $::saved_counter(reported_up)}]]\n"


    set l [ttk::label $f.stats -text $stats]
    grid $l


    focus $t
}




proc show_about {} {

    if {[info commands .about] ne {}} {
        destroy .about
    }

    set t [toplevel .about]
    wm title $t "About Ratio Ghost"

    wm resizable $t 0 0

    set f [ttk::frame $t.f -padding 20]
    grid $f -sticky nsew

    set about ""
    append about "Ratio Ghost v$::version\n"
    append about "Build $::build\n"
    append about "Copyright (C) 2006-2026\n"
    append about "Project: https://github.com/Mac-Cipher/RatioGhost\n\n"

    set cert_status "Not Found"
    if {[info exists ::cert_path] && [file exists $::cert_path] && [file size $::cert_path] > 0} {
        set cert_status "OK ([FormatData [file size $::cert_path]])"
    }
    append about "TLS Intercept Cert: $cert_status"

    set l [ttk::label $f.about -text $about -justify center]
    grid $l -pady 5

    set link [ttk::label $f.link -text "GitHub Repository" -foreground "#569CD6" -cursor hand2]
    bind $link <Button-1> {OpenDocument https://github.com/Mac-Cipher/RatioGhost}
    grid $link -pady 5

    focus $t
}


proc show_website {} {
    OpenDocument https://github.com/Mac-Cipher/RatioGhost
}
