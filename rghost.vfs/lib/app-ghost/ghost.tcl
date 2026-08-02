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


set build 522
set version_file [file join $::rg_dir VERSION]
if {![file isfile $version_file]} {
    return -code error "Missing application version file: $version_file"
}
set version_fd [open $version_file r]
try {
    set version [string trim [read $version_fd]]
} finally {
    close $version_fd
}
if {![regexp {^[0-9]+(?:\.[0-9]+){2}(?:[-+][0-9A-Za-z.-]+)?$} $version]} {
    return -code error "Invalid application version in $version_file: $version"
}


package provide app-ghost 1.0

package require Tk
package require app-util
package require app-gui
package require app-proxy
package require app-update
package require md5

update idletasks

proc logerror {message} {
    set fname [file join [GetProfileDirectory] bgerror.txt]
    set o [open $fname a]
    puts $o "\n\n[clock format [clock seconds]] ([pid])\n$message\n\n"
    close $o
}


set ProcessingError 0
proc bgerror {message} {
    global ProcessingError
    if {$ProcessingError} return
    set ProcessingError 1

    # Ignore common network/TLS connection errors to prevent log flooding and GUI freezes
    if {[regexp -nocase {ssl channel|sslv3 alert|handshake failure|connection reset|broken pipe|connection refused} $message]} {
        set ProcessingError 0
        return
    }

    set em "Background error: $message\n\n$::errorInfo"

    #tk_messageBox -title "Application Error" -message $em

    logerror $em

    set ProcessingError 0
}


puts $::argv


set setting_file [file join [GetProfileDirectory] settings.dat]


proc GetLaunchCommand {} {
    set executable [file nativename [info nameofexecutable]]
    set command "\"$executable\""
    if {[string match -nocase "tclkit*.exe" [file tail $executable]]} {
        append command " \"[file nativename [file join $::rg_dir main.tcl]]\""
    }
    append command " m"
    return $command
}

proc ApplyAutostart {args} {
    if {!$::WINDOWS} return
    if {[catch {package require registry} err]} {
        puts "Error: registry package not available: $err"
        return
    }
    set regPath "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
    if {$::settings(autostart)} {
        set cmd [GetLaunchCommand]
        if {[catch {registry set $regPath "RatioGhost" $cmd} err]} {
            puts "Error setting registry: $err"
        }
    } else {
        if {[catch {registry delete $regPath "RatioGhost"} err]} {
            # Might not exist, which is fine
        }
    }
    catch {SaveSettings}
}

proc SyncAutostart {} {
    if {!$::WINDOWS} return
    if {[catch {package require registry} err]} return
    set regPath "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
    set expected [GetLaunchCommand]
    if {![catch {set val [registry get $regPath "RatioGhost"]}]} {
        if {$::settings(autostart)} {
            catch {registry set $regPath "RatioGhost" $expected}
        } else {
            set ::settings(autostart) 1
        }
    } elseif {$::settings(autostart)} {
        catch {registry set $regPath "RatioGhost" $expected}
    }
}



proc LoadSettings {} {
    global setting_file
    set loaded 0
    if {[file exists $setting_file]} {
        if {[catch {
            set si [open $setting_file r]
            set d [read $si]
            close $si
            array set ::settings $d
            set loaded 1
        } err]} {
            puts "Warning: Could not load settings: $err"
            # Try loading from backup
            set bak_file "$setting_file.bak"
            if {[file exists $bak_file]} {
                if {[catch {
                    set si [open $bak_file r]
                    set d [read $si]
                    close $si
                    array set ::settings $d
                    set loaded 1
                    puts "Loaded settings from backup."
                } err2]} {
                    puts "Warning: Could not load backup settings: $err2"
                }
            }
        }
    }

    if {!$loaded} {
        array set ::settings {}
    }


    set defaults {}
    lappend defaults first [clock seconds]

    lappend defaults id [::md5::md5 -hex "[clock seconds]$::tcl_platform(user)"]

    lappend defaults runtime 0
    lappend defaults sessions 0

    set default_listen_port 3773
    if {[info exists ::env(RATIOGHOST_LISTEN_PORT)] &&
        [string is integer -strict $::env(RATIOGHOST_LISTEN_PORT)] &&
        $::env(RATIOGHOST_LISTEN_PORT) >= 1 && $::env(RATIOGHOST_LISTEN_PORT) <= 65534} {
        set default_listen_port $::env(RATIOGHOST_LISTEN_PORT)
    }
    lappend defaults listen_port $default_listen_port
    lappend defaults listen_port_https [expr {$default_listen_port + 1}]
    lappend defaults only_tracker 1
    lappend defaults only_local 1
    lappend defaults proxy_debug_logging 0
    lappend defaults update 1
    lappend defaults autostart 0
    lappend defaults start_minimized 0

    lappend defaults min_peers 5
    lappend defaults upup_ratio_a 4.0
    lappend defaults upup_ratio_b 8.0
    lappend defaults updown_ratio_a 0.00
    lappend defaults updown_ratio_b 0.05

    lappend defaults boost 15
    lappend defaults boost_chance 5

    lappend defaults no_download 0
    lappend defaults seed 0

    lappend defaults actual_down 0
    lappend defaults reported_down 0

    lappend defaults actual_up 0
    lappend defaults reported_up 0

    foreach {k v} $defaults {
        if {![info exists ::settings($k)]} {
            set ::settings($k) $v
        }
    }

    set ::settings(start) [clock seconds]
    incr ::settings(sessions)
    set ::last_save_time $::settings(start)
    foreach key {actual_down actual_up reported_down reported_up} {
        set ::saved_counter($key) 0
    }
}


proc SaveSettings {} {
    global setting_file

    array set s [array get ::settings]
    array set current [list \
        actual_down $::actual_down actual_up $::actual_up \
        reported_down $::reported_down reported_up $::reported_up]
    array set baseline [array get ::saved_counter]
    set now [clock seconds]
    AddSessionTotals s current baseline [expr {$now - $::last_save_time}]

    set s(geometry) [wm geometry .]

    # Atomic save: write to .tmp, then rename with .bak backup
    set tmp_file "$setting_file.tmp"
    set bak_file "$setting_file.bak"
    if {[catch {
        set si [open $tmp_file w]
        puts -nonewline $si [array get s]
        close $si
        # Create backup of current settings
        if {[file exists $setting_file]} {
            catch {file copy -force $setting_file $bak_file}
        }
        file rename -force $tmp_file $setting_file
        array set ::settings [array get s]
        array set ::saved_counter [array get baseline]
        set ::last_save_time $now
    } err]} {
        puts "Warning: Could not save settings: $err"
        catch {file delete $tmp_file}
    }
}


proc Close {} {
    set r [tk_messageBox -title "Ratio Ghost" -message "Are you sure you want to exit Ratio Ghost?" -type yesno -default no]

    if {$r eq "yes"} {
        Kill
        exit 0
    }
}


set dead 0
proc Kill {} {
    global dead icon

    if {!$dead} {
        set dead 1

        SaveSettings

        if {$::WINDOWS} {
            winico taskbar delete $icon
        }
    }
}




proc update_status {} {
    catch {after cancel $::update_status_after}
    set ::update_status_after [after 2000 update_status]

    global status
    global actual_up actual_down reported_up reported_down

    # Always compute counters (even when minimized) so SaveSettings has correct values
    set actual_up 0
    set actual_down 0
    set reported_up 0
    set reported_down 0
    set torrents 0
    set waiting 0
    set last_announce 0

    foreach h [array names ::actual_sum] {
        set act $::actual_sum($h)

        lassign $act d u

        incr actual_down $d
        incr actual_up $u

        incr torrents

        if {[info exists ::response($h,incomplete)] && $::response($h,incomplete) < $::settings(min_peers)} {
            incr waiting
        }
        if {[info exists ::reported_last_time($h)] && $::reported_last_time($h) > $last_announce} {
            set last_announce $::reported_last_time($h)
        }
    }

    foreach h [array names ::reported_sum] {
        set rep $::reported_sum($h)

        lassign $rep d u

        incr reported_down $d
        incr reported_up $u
    }

    set auto_seed_only 0
    if {[info commands update_auto_seed_only] ne ""} {
        set auto_seed_only [update_auto_seed_only]
    }

    # Skip UI update when window is withdrawn (minimized to tray)
    if {[wm state .] eq "withdrawn"} {return}

    set elapsed [expr {[clock seconds] - $::settings(start)}]
    set status "Uptime: [FormatElapsed $elapsed]   |   "
    append status "Torrents: $torrents   |   "
    if {$last_announce > 0} {
        append status "Last announce: [clock format $last_announce -format %H:%M:%S]"
    } else {
        append status "Last announce: -"
    }

    # Show pause state in status
    if {[info exists ::paused] && $::paused} {
        append status "   |   \u23F8 PAUSED"
    } elseif {$auto_seed_only} {
        append status "   |   Mode: seed-only standby"
    } elseif {$torrents > 0 && $waiting == $torrents} {
        append status "   |   Mode: waiting for leechers"
    } else {
        append status "   |   Mode: active"
    }
}



# Generate a unique TLS certificate only when local TLS interception is enabled.
set profile_dir [GetProfileDirectory]
set target_tls_dir [file join $profile_dir tls]
if {![file isdirectory $target_tls_dir]} {
    catch {file mkdir $target_tls_dir}
}

if {![info exists ::rg_dir]} {
    set ::rg_dir [file normalize [file join [file dirname [info script]] .. ..]]
}

set ::cert_path [file join $target_tls_dir server.crt]
set ::key_path [file join $target_tls_dir server.key]
set cert_marker [file join $target_tls_dir unique-certificate-v2]

if {[info exists ::enable_tls_interception] && $::enable_tls_interception} {
    if {![file exists $cert_marker] ||
        ![file exists $::cert_path] || [file size $::cert_path] == 0 ||
        ![file exists $::key_path] || [file size $::key_path] == 0} {
        catch {file delete -force $::cert_path $::key_path}
        if {[catch {
            GenerateTlsCertificate $::cert_path $::key_path
            close [open $cert_marker w]
        } cert_err]} {
            tk_messageBox -icon error -title "Ratio Ghost TLS Error" \
                -message "Could not generate a unique TLS certificate:\n$cert_err"
            exit 1
        }
    }
}

LoadSettings
CreateGui

set isolated_test [expr {
    [info exists ::env(RATIOGHOST_ISOLATED_TEST)] &&
    $::env(RATIOGHOST_ISOLATED_TEST) eq "1"
}]
if {$::WINDOWS && !$isolated_test} {
    trace add variable ::settings(autostart) write ApplyAutostart
    SyncAutostart
}

update_status


if {$::settings(start_minimized) || ($::argc > 0 && [lindex $::argv 0] eq "m")} {
    Hide
}


proc ListenSettingChanged {args} {
    if {[info exists ::listen_reconfiguring] && $::listen_reconfiguring} {
        return
    }
    if {[catch {listen} listen_err]} {
        Event $listen_err
    }
}

if {[catch {listen} listen_err]} {
    tk_messageBox -icon warning -title "Ratio Ghost" \
        -message "$listen_err\n\nRatio Ghost is probably already running. Use the tray icon to show the existing window, or exit the existing copy before starting a new one."
    Kill
    exit 0
}
trace add variable ::settings(listen_port) write ListenSettingChanged
trace add variable ::settings(only_local) write ListenSettingChanged



proc SaveOften {} {
    SaveSettings
    after 3600000 SaveOften
}

after 1800000 SaveOften
