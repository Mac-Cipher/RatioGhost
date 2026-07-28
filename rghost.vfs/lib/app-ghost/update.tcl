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


package provide app-update 1.0
package require app-util
package require http
package require tls

# Register HTTPS only with certificate-chain and hostname validation enabled.
set ::update_tls_available 0
if {![catch {
    set update_tls_options [TlsClientOptions api.github.com]
    ::http::register https 443 [list ::tls::socket {*}$update_tls_options]
    set ::update_tls_available 1
} update_tls_error]} {
    unset -nocomplain update_tls_error
}

set os NA
if {$::WINDOWS} {set os W}
if {$::LINUX} {set os L}
if {$::MAC} {set os M}

::http::config -useragent "RATIO-GHOST-$os-$build"

set check 0
set skip_update 0
set checking_update 0

proc extract_latest_version {data} {
    if {[regexp -nocase {"tag_name"[[:space:]]*:[[:space:]]*"v?([^"]+)"} $data _ latest_version]} {
        return $latest_version
    }
    return {}
}

proc check_for_updates {} {
    global build check
    incr check
    set url "https://api.github.com/repos/Mac-Cipher/RatioGhost/releases/latest"
    if {[info exists ::settings(update)] && $::settings(update) && $::update_tls_available} {
        puts "Checking for updates on GitHub..."
        if {[set ec [catch {set r [::http::geturl $url -command update_complete -timeout 30000]} err]]} {
            puts "Couldn't check for update:"
            puts "error:$ec $err"
        }
    } elseif {[info exists ::settings(update)] && $::settings(update) && !$::update_tls_available} {
        puts "Skipping update check because secure TLS validation is unavailable."
    }
    # Check every 12 hours
    after [expr {1000 * 60 * 60 * 12}] check_for_updates
}

proc update_complete {r} {
    if {$::skip_update || $::checking_update} {
        ::http::cleanup $r
        return
    }

    set ::checking_update 1

    try {
        set ncode [::http::ncode $r]
        set data [::http::data $r]

        puts "Received update info: $ncode"

        if {$ncode == 200} {
            set latest_version [extract_latest_version $data]
            if {$latest_version ne ""} {
                puts "Latest version on GitHub is $latest_version (current is $::version)"
                if {[version_compare $::version $latest_version]} {
                    set res [tk_messageBox -title "Ratio Ghost Update Available" \
                        -message "There is a new version of Ratio Ghost available (v$latest_version). Updating to the latest version is highly recommended. Would you like to view the releases page now?" \
                        -type yesno -icon info]
                    if {$res eq {yes}} {
                        OpenDocument https://github.com/Mac-Cipher/RatioGhost/releases
                    } else {
                        set ::skip_update 1
                    }
                }
            }
        }
    } finally {
        set ::checking_update 0
        ::http::cleanup $r
    }
}

# Start checking for updates after 15 seconds
after 15000 check_for_updates
