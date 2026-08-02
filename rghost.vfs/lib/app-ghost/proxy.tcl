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




# This code is a huge mess. It's so bad that I think you deserve an explanation.
# Here's how it ended up this way:
# 1) There was no planning. This all grew organically over many years.
# 2) I support a lot of torrent clients and sites, and it turns out that many
#    do things that could've been standardized a bit differently.
# 3) TCL is finicky. The sockets behave odd, and when using TLS they work differently.
# 4) I'm just not a great programmer.
# That said, this should probably be refatored someday into cleaner code, but it's
# a deceptively big task because being compatible with many torrent clients and
# trackers isn't obvious, and small details matter.




package provide app-proxy 1.0

set auto_path [linsert $auto_path 0 .]
package require tls



expr {srand([clock seconds])}
set ::paused 0
# The paused override is intentionally limited to isolated characterization
# runs. It lets the .NET migration replay the legacy state machine without
# driving the interactive Tcl/Tk control surface or changing user settings.
if {[info exists ::env(RATIOGHOST_ISOLATED_TEST)] &&
    $::env(RATIOGHOST_ISOLATED_TEST) eq "1" &&
    [info exists ::env(RATIOGHOST_ISOLATED_PAUSED)] &&
    $::env(RATIOGHOST_ISOLATED_PAUSED) eq "1"} {
    set ::paused 1
}
set ::isolated_random_values {}
set ::isolated_random_index 0
if {[info exists ::env(RATIOGHOST_ISOLATED_TEST)] &&
    $::env(RATIOGHOST_ISOLATED_TEST) eq "1" &&
    [info exists ::env(RATIOGHOST_ISOLATED_RANDOM_SEQUENCE)]} {
    foreach candidate [split $::env(RATIOGHOST_ISOLATED_RANDOM_SEQUENCE) ,] {
        if {[string is double -strict $candidate] &&
            $candidate >= 0.0 && $candidate < 1.0} {
            lappend ::isolated_random_values $candidate
        }
    }
}

proc RatioGhostRandom {} {
    global isolated_random_values isolated_random_index
    if {$isolated_random_index < [llength $isolated_random_values]} {
        set value [lindex $isolated_random_values $isolated_random_index]
        incr isolated_random_index
        return $value
    }
    return [expr {rand()}]
}

set ::auto_seed_only 0
set ::active_connection_count 0
set ::max_active_connections 32
# Enable TLS interception so HTTPS tracker announces can be modified.
# Without this, CONNECT tunnels bypass all ratio modification.
set ::enable_tls_interception 1



proc prox_https {local addr port} {
    if {![info exists ::enable_tls_interception] || !$::enable_tls_interception} {
        Event "Direct HTTPS listener is disabled; use HTTP proxy port $::settings(listen_port) for CONNECT tunnels."
        catch {fconfigure $local -blocking 0}
        catch {close $local}
        return
    }
    prox $local $addr $port 1
}


proc prox {local addr port {https 0}} {
    global linfo lhost

    if {$::active_connection_count >= $::max_active_connections} {
        catch {fconfigure $local -blocking 0}
        catch {close $local}
        return
    }

    incr ::active_connection_count
    set ::active_local($local) 1
    dlog_new
    set ::connection_log($local) $log_num
    set ::first_line_timeout($local) [after 5000 [list file_line_timeout $local]]

    set is_remote_client [expr {![string match "127.*" $addr] && $addr ne "::1" && $addr ne "0:0:0:0:0:0:0:1"}]
    set ::connection_is_remote($local) $is_remote_client
    if {$::settings(only_local)} {
        if {$is_remote_client} {
            Event "Blocked request from $addr:$port."
            cleanup_connection $local
            return
        }
    }

    # Rate limiting: max 50 connections per second
    set now [clock seconds]
    if {![info exists ::rate_limit_time] || $::rate_limit_time != $now} {
        set ::rate_limit_time $now
        set ::rate_limit_count 0
    }
    incr ::rate_limit_count
    if {$::rate_limit_count > 50} {
        dlog "Rate limit exceeded ($::rate_limit_count connections in 1s)"
        cleanup_connection $local
        return
    }

    if {$https} {
        fconfigure $local -translation binary
        fconfigure $local -buffering none -blocking 0
        dlog "Initializing TLS"
        if {[catch {
            tls::import $local -certfile $::cert_path -keyfile $::key_path -server true -ssl2 0 -ssl3 0 -tls1 0 -tls1.1 0 -tls1.2 1
        } err]} {
            dlog "TLS import failed: $err"
            cleanup_connection $local
            return
        }
        fconfigure $local -blocking 0
        dlog "Accept $local from $addr port $port (https)"
    } else {
        fconfigure $local -translation binary -blocking 0 -buffering none
        dlog "Accept $local from $addr port $port"
    }


    fileevent $local readable [list read_first $log_num $local $https]

    set linfo($local) $addr:$port
    set lhost($local) ""

    dlog "finish prox"
}


proc stats_actual {hash event down up left} {
    global actual_first actual_last actual_sum
    upvar 1 local local

    foreach v [list actual_previous_down actual_previous_up actual_previous_down_diff actual_previous_up_diff] {
        upvar 1 $v $v
        set $v 0
    }

    #puts "Updating actual stats on $hash ($down $up $left)"

    if {![info exists actual_first($hash)]} {
        set actual_first($hash) [list $down $up $left]
    }

    if {![info exists actual_sum($hash)]} {
        set actual_sum($hash) "0 0"
    }

    if {[info exists actual_last($hash)]} {
        if {$event ne {started}} {
            lassign $actual_last($hash) d u l
            lassign $actual_sum($hash) ad au
            set actual_sum($hash) "[expr {($down-$d)+$ad}] [expr {($up-$u)+$au}]"

            set actual_previous_down $d
            set actual_previous_up $u
        }
    }

    set actual_previous_down_diff [expr {$down - $actual_previous_down}]
    set actual_previous_up_diff [expr {$up - $actual_previous_up}]

    set actual_last($hash) [list $down $up $left]
}


proc stats_reported {hash event down up left} {
    global reported_last reported_sum reported_last_time
    upvar 1 local local

    #puts "Updating reported stats on $hash ($down $up $left)"

    if {![info exists reported_sum($hash)]} {
        set reported_sum($hash) "0 0"
    }

    if {[info exists reported_last($hash)]} {
        if {$event ne {started}} {
            lassign $reported_last($hash) d u l
            lassign $reported_sum($hash) ad au
            set reported_sum($hash) "[expr {($down-$d)+$ad}] [expr {($up-$u)+$au}]"
        }
    }

    set reported_last_time($hash) [clock seconds]
    set reported_last($hash) [list $down $up $left]
}


proc left_means_downloading {left} {
    return [expr {[string is double -strict $left] && $left > 0}]
}


proc has_active_downloads {} {
    foreach hash [array names ::actual_last] {
        if {[llength $::actual_last($hash)] < 3} {
            continue
        }
        set left [lindex $::actual_last($hash) 2]
        if {[left_means_downloading $left]} {
            return 1
        }
    }
    return 0
}


proc update_auto_seed_only {} {
    set had_auto_seed_only [expr {[info exists ::auto_seed_only] && $::auto_seed_only}]
    set has_torrents [expr {[array size ::actual_last] > 0}]
    set ::auto_seed_only [expr {$has_torrents && ![has_active_downloads]}]

    if {$had_auto_seed_only != $::auto_seed_only && [info commands Event] ne ""} {
        if {$::auto_seed_only} {
            Event "Auto seed-only standby - all known torrents are complete"
        } else {
            Event "Auto seed-only standby ended - download activity detected"
        }
    }

    return $::auto_seed_only
}


set log_inc 1
proc dlog_new {} {
    upvar 1 log_num log_num
    incr ::log_inc
    set log_num $::log_inc
}


proc dlog {what} {
    upvar 1 log_num log_num
    if {![info exists log_num]} {
        set log_num 0
    }
    if {$log_num > 0} {
        append ::event_log($log_num) "$what\n"
    }
    if {![info exists ::settings(proxy_debug_logging)] || !$::settings(proxy_debug_logging)} {
        return
    }

    set path [proxy_debug_log_path]
    if {[file exists $path] && [file size $path] >= 1048576} {
        catch {file rename -force $path "$path.1"}
    }
    set fd ""
    try {
        set fd [open $path a]
        puts $fd "[clock format [clock seconds] -format {%Y-%m-%d %H:%M:%S}] ($log_num): [redact_sensitive $what]"
    } on error {} {
        # Debug logging must never disrupt proxy traffic.
    } finally {
        if {$fd ne ""} {catch {close $fd}}
    }
}

proc proxy_debug_log_path {} {
    if {[info commands GetProfileDirectory] ne ""} {
        return [file join [GetProfileDirectory] proxy_debug.log]
    }
    if {[info exists ::env(APPDATA)]} {
        set dir [file join $::env(APPDATA) RatioGhost]
        catch {file mkdir $dir}
        return [file join $dir proxy_debug.log]
    }
    return [file join [pwd] proxy_debug.log]
}


proc apply_download_reporting_options {fake info_hash event downloaded left} {
    if {$::settings(no_download)} {
        lassign $::actual_first($info_hash) d u l
        dlog "Loaded first stats: $d $u $l"

        set downloaded 0
        set left $l

        if {$event eq {completed}} {
            dlog "Blocking completed event"
            set fake [rewrite_query_params $fake {} {event}]
        }
    }

    if {$::settings(seed)} {
        set left 0
    }

    return [list $fake $downloaded $left]
}


proc dlog_set {where} {
    upvar 1 log_num log_num
    set ::log_lookup($where) $log_num
    set ::log_referenced($log_num) 1
}


# Centralized connection cleanup — ensures all tracking arrays are cleared
proc cleanup_connection {local {remote ""}} {
    if {$remote eq "" && [info exists ::local_remote($local)]} {
        set remote $::local_remote($local)
    }

    catch {fileevent $local readable ""}
    catch {fileevent $local writable ""}
    catch {fconfigure $local -blocking 0}
    catch {close $local}
    if {[info exists ::active_local($local)]} {
        unset ::active_local($local)
        if {$::active_connection_count > 0} {
            incr ::active_connection_count -1
        }
    }
    catch {unset ::linfo($local)}
    catch {unset ::lhost($local)}
    catch {unset ::intercept_real_host($local)}
    catch {unset ::connection_is_remote($local)}
    catch {unset ::first_line_accumulator($local)}
    catch {unset ::connect_authority($local)}
    catch {unset ::connect_header_accumulator($local)}
    catch {after cancel $::first_line_timeout($local)}
    catch {unset ::first_line_timeout($local)}
    catch {after cancel $::connect_header_timeout($local)}
    catch {unset ::connect_header_timeout($local)}
    catch {after cancel $::first_read_retry($local)}
    catch {unset ::first_read_retry($local)}
    catch {after cancel $::local_read_retry($local)}
    catch {unset ::local_read_retry($local)}
    catch {unset ::local_remote($local)}
    catch {unset ::extra_data($local)}
    catch {unset ::real_hostname($local)}
    catch {unset ::response_accumulator($remote)}
    catch {unset ::response_event_summary($remote)}
    if {[info exists ::connection_log($local)]} {
        set log_num $::connection_log($local)
        unset -nocomplain ::hash_lookup($log_num)
        if {![info exists ::log_referenced($log_num)]} {
            unset -nocomplain ::event_log($log_num)
        }
        unset ::connection_log($local)
    }
    if {$remote ne ""} {
        catch {fileevent $remote readable ""}
        catch {fileevent $remote writable ""}
        catch {fconfigure $remote -blocking 0}
        catch {close $remote}
        catch {unset ::rinfo($remote)}
        catch {unset ::first($remote)}
        catch {after cancel $::socket_timeout($remote)}
        catch {unset ::socket_timeout($remote)}
        catch {after cancel $::remote_read_retry($remote)}
        catch {unset ::remote_read_retry($remote)}
        catch {unset ::remote_local($remote)}
    }
}

proc arm_socket_timeout {remote local {ei ""}} {
    catch {after cancel $::socket_timeout($remote)}
    set ::socket_timeout($remote) [after 45000 [list socket_timeout $remote $local $ei]]
}

proc redact_sensitive {text} {
    regsub -all -nocase {(info_hash|passkey|authkey|token|peer_id|key|ip|ipv4|ipv6)=([^&[:space:]]+)} $text {\1=<redacted>} text
    regsub -all {(/[A-Za-z0-9._~-]{20,})(/|[?[:space:]]|$)} $text {/<redacted>\2} text
    return $text
}

proc summarize_request_target {target} {
    set hash_index [string first # $target]
    if {$hash_index >= 0} {
        set target [string range $target 0 [expr {$hash_index - 1}]]
    }
    set query_index [string first ? $target]
    if {$query_index >= 0} {
        set target [string range $target 0 [expr {$query_index - 1}]]
    }
    return [redact_sensitive $target]
}

proc validate_proxy_port {port} {
    if {![string is integer -strict $port]} {return 0}
    return [expr {$port >= 1 && $port <= 65535}]
}

proc split_url_authority_rest {tail} {
    set cut -1
    foreach char [list / ? #] {
        set idx [string first $char $tail]
        if {$idx >= 0 && ($cut < 0 || $idx < $cut)} {
            set cut $idx
        }
    }

    if {$cut < 0} {
        return [list $tail /]
    }

    set authority [string range $tail 0 [expr {$cut - 1}]]
    set suffix [string range $tail $cut end]
    if {[string index $suffix 0] eq "/"} {
        set rest $suffix
    } else {
        set rest "/$suffix"
    }
    return [list $authority $rest]
}

proc parse_authority_host_port {authority default_port} {
    if {$authority eq ""} {return {}}

    if {[regexp {^\[([^\]]+)\](?::([0-9]+))?$} $authority _ host port]} {
        if {$port eq ""} {set port $default_port}
        if {![validate_proxy_port $port]} {return {}}
        return [list $host $port]
    }

    if {[regexp {^([^:]+)(?::([0-9]+))?$} $authority _ host port]} {
        if {$port eq ""} {set port $default_port}
        if {![validate_proxy_port $port]} {return {}}
        return [list $host $port]
    }

    return {}
}

proc parse_proxy_url {url https} {
    if {![regexp -nocase {^([a-z][a-z0-9+.-]*)://(.+)$} $url _ scheme tail]} {
        return {}
    }

    if {[string equal -nocase $scheme "https"]} {
        set https 1
    } elseif {![string equal -nocase $scheme "http"]} {
        return {}
    }

    lassign [split_url_authority_rest $tail] authority rest
    set default_port [expr {$https ? 443 : 80}]
    set parsed [parse_authority_host_port $authority $default_port]
    if {$parsed eq {}} {return {}}
    lassign $parsed host port
    return [list $host $port $rest $https]
}

proc parse_connect_authority {authority} {
    if {[regexp {^\[([^\]]+)\]:([0-9]+)$} $authority _ host port]} {
        if {[validate_proxy_port $port]} {
            return [list $host $port]
        }
        return {}
    }
    if {![regexp {^([^:]+):([0-9]+)$} $authority _ host port]} {
        return {}
    }
    if {![validate_proxy_port $port]} {return {}}
    return [list $host $port]
}

proc split_connect_headers {data} {
    if {[string first "\r\n" $data] == 0} {
        return [list 1 [string range $data 2 end]]
    }
    if {[string first "\n" $data] == 0} {
        return [list 1 [string range $data 1 end]]
    }
    foreach delimiter [list "\r\n\r\n" "\n\n"] {
        set idx [string first $delimiter $data]
        if {$idx >= 0} {
            set payload [string range $data [expr {$idx + [string length $delimiter]}] end]
            return [list 1 $payload]
        }
    }
    return [list 0 {}]
}

proc should_intercept_connect {port} {
    return [expr {[info exists ::enable_tls_interception] && $::enable_tls_interception && $port == 443}]
}

proc request_must_be_tracker {local} {
    return [expr {
        ([info exists ::settings(only_tracker)] && $::settings(only_tracker)) ||
        ([info exists ::connection_is_remote($local)] && $::connection_is_remote($local))
    }]
}

proc transparent_connect_allowed {local} {
    return [expr {![request_must_be_tracker $local]}]
}

proc format_host_header {host port} {
    if {[string first : $host] >= 0 && ![string match {\[*\]} $host]} {
        set host "\[$host\]"
    }
    if {$port != 80} {
        return "$host:$port"
    }
    return $host
}

proc parse_host_header_value {value} {
    set value [string trim $value]
    if {$value eq ""} {return {}}

    if {[regexp {^\[([^\]]+)\](?::([0-9]+))?$} $value _ host port]} {
        if {$port ne "" && ![validate_proxy_port $port]} {return {}}
        return [list $host $port]
    }

    set colon_count [regexp -all {:} $value]
    if {$colon_count == 0} {
        return [list $value {}]
    }
    if {$colon_count == 1 && [regexp {^([^:]+):([0-9]+)$} $value _ host port]} {
        if {![validate_proxy_port $port]} {return {}}
        return [list $host $port]
    }

    return [list $value {}]
}

proc extract_host_header {headers} {
    if {[regexp -line -nocase {^Host:[ \t]*([^\r\n]+)} $headers _ value]} {
        return [parse_host_header_value $value]
    }
    return {}
}

proc split_resource_query {resource} {
    set fragment ""
    set hash_idx [string first # $resource]
    if {$hash_idx >= 0} {
        set fragment [string range $resource $hash_idx end]
        set base [string range $resource 0 [expr {$hash_idx - 1}]]
    } else {
        set base $resource
    }

    set query_idx [string first ? $base]
    if {$query_idx < 0} {
        return [list $base "" $fragment 0]
    }

    set prefix [string range $base 0 [expr {$query_idx - 1}]]
    set query [string range $base [expr {$query_idx + 1}] end]
    return [list $prefix $query $fragment 1]
}

proc query_pair_name {pair} {
    set eq_idx [string first = $pair]
    if {$eq_idx < 0} {return $pair}
    return [string range $pair 0 [expr {$eq_idx - 1}]]
}

proc query_pair_value {pair} {
    set eq_idx [string first = $pair]
    if {$eq_idx < 0} {return ""}
    return [string range $pair [expr {$eq_idx + 1}] end]
}

proc query_name_matches {actual expected} {
    return [string equal -nocase $actual $expected]
}

proc parse_query_params {resource} {
    lassign [split_resource_query $resource] prefix query fragment has_query
    set params {}
    if {!$has_query} {return $params}
    foreach pair [split $query &] {
        if {$pair eq ""} continue
        dict set params [query_pair_name $pair] [query_pair_value $pair]
    }
    return $params
}

proc query_dict_get_nocase {params name} {
    foreach key [dict keys $params] {
        if {[query_name_matches $key $name]} {
            return [dict get $params $key]
        }
    }
    return {}
}

proc query_dict_exists_nocase {params name} {
    foreach key [dict keys $params] {
        if {[query_name_matches $key $name]} {
            return 1
        }
    }
    return 0
}

proc query_list_contains_nocase {names name} {
    foreach candidate $names {
        if {[query_name_matches $candidate $name]} {
            return 1
        }
    }
    return 0
}

proc rewrite_query_params {resource updates removals} {
    lassign [split_resource_query $resource] prefix query fragment has_query
    if {!$has_query} {return $resource}

    set out {}
    foreach pair [split $query &] {
        if {$pair eq ""} {
            lappend out $pair
            continue
        }
        set name [query_pair_name $pair]
        if {[query_list_contains_nocase $removals $name]} {
            continue
        }
        set replaced 0
        foreach update_name [dict keys $updates] {
            if {[query_name_matches $update_name $name]} {
                lappend out "$name=[dict get $updates $update_name]"
                set replaced 1
                break
            }
        }
        if {!$replaced} {
            lappend out $pair
        }
    }

    return "$prefix?[join $out &]$fragment"
}

proc rewrite_loopback_host_headers {data host_header listen_port listen_port_https} {
    foreach port [list $listen_port $listen_port_https] {
        regsub -all -nocase "Host:\[ \t\]*127\\.0\\.0\\.1:$port" $data "Host: $host_header" data
        regsub -all -nocase "Host:\[ \t\]*localhost:$port" $data "Host: $host_header" data
    }
    return $data
}

proc connect_header_timeout {local} {
    if {[info exists ::connect_header_accumulator($local)]} {
        cleanup_connection $local
    }
}

proc start_connect_tunnel {log_num local authority} {
    set parsed [parse_connect_authority $authority]
    if {$parsed eq {}} {
        dlog "Invalid CONNECT authority"
        cleanup_connection $local
        return
    }

    set ::connect_authority($local) $parsed
    set ::connect_header_accumulator($local) ""
    if {[info exists ::extra_data($local)]} {
        append ::connect_header_accumulator($local) $::extra_data($local)
        unset ::extra_data($local)
    }
    set ::connect_header_timeout($local) [after 3000 [list connect_header_timeout $local]]
    consume_connect_headers $log_num $local
}

proc consume_connect_headers {log_num local} {
    if {[eof $local]} {
        cleanup_connection $local
        return
    }
    if {[catch {set data [read $local]} err]} {
        dlog "Error reading CONNECT headers: $err"
        cleanup_connection $local
        return
    }

    append ::connect_header_accumulator($local) $data
    if {[string length $::connect_header_accumulator($local)] > 65536} {
        dlog "CONNECT headers exceeded 64 KiB"
        cleanup_connection $local
        return
    }

    lassign [split_connect_headers $::connect_header_accumulator($local)] complete payload
    if {!$complete} {
        fileevent $local readable [list consume_connect_headers $log_num $local]
        return
    }

    fileevent $local readable ""
    catch {after cancel $::connect_header_timeout($local)}
    unset -nocomplain ::connect_header_timeout($local) ::connect_header_accumulator($local)
    lassign $::connect_authority($local) host port
    unset ::connect_authority($local)

    if {[should_intercept_connect $port]} {
        dlog "Intercepting HTTPS CONNECT to $host:$port"
        if {$payload ne ""} {
            dlog "CONNECT had early TLS payload; blocking to avoid unmodified tracker announce"
            set ei [Event "Blocked HTTPS tracker tunnel to $host:$port (early TLS payload)"]
            dlog_set $ei
            cleanup_connection $local
            return
        }

        if {[catch {
            puts -nonewline $local "HTTP/1.1 200 Connection Established\r\n\r\n"
            flush $local
            fconfigure $local -translation binary -buffering none -blocking 0
            tls::import $local \
                -certfile $::cert_path \
                -keyfile  $::key_path \
                -server   true \
                -ssl2 0 -ssl3 0 -tls1 0 -tls1.1 0 -tls1.2 1
            fconfigure $local -blocking 0
        } err]} {
            dlog "TLS CONNECT interception failed: $err"
            set ei [Event "Blocked HTTPS tracker tunnel to $host:$port (TLS intercept failed)"]
            dlog_set $ei
            cleanup_connection $local
            return
        }

        set ::intercept_real_host($local) [format_host_header $host $port]
        fileevent $local readable [list read_first $log_num $local 1]
        return
    }

    if {![transparent_connect_allowed $local]} {
        dlog "Blocking transparent CONNECT to $host:$port while tracker-only mode is enabled"
        set ei [Event "Blocked non-tracker tunnel to $host:$port"]
        dlog_set $ei
        catch {
            puts -nonewline $local "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n"
            flush $local
        }
        cleanup_connection $local
        return
    }

    set ei [Event "Transparent HTTPS tunnel to $host:$port"]
    dlog_set $ei
    route_tunnel $local $host $port $log_num $ei $payload
}

proc file_line_timeout {local} {
    if {[info exists ::first_line_accumulator($local)]} {
        cleanup_connection $local
    }
}


array set hosts {}

proc read_first {log_num local https} {
    global rinfo first linfo lhost

    dlog "read_first $log_num $local $https"
    fileevent $local readable ""

    # BUG FIX: check for HTTPS CONNECT tunnel BEFORE reading any bytes.
    # If lhost is set to host:443 we are inside a CONNECT tunnel and the next
    # bytes will be a TLS ClientHello.  We must NOT consume those bytes before
    # calling tls::import, otherwise the TLS handshake is corrupted.
    if {$::enable_tls_interception && !$https && $lhost($local) ne ""} {
        set to [split $lhost($local) :]
        if {[llength $to] == 2} {
            lassign $to tohost toport
            if {$toport == 443} {
                dlog "HTTPS CONNECT pre-read interception for $tohost:443"
                set tls_ok 1
                if {[catch {
                    fconfigure $local -translation binary -buffering none -blocking 0
                    tls::import $local \
                        -certfile $::cert_path \
                        -keyfile  $::key_path \
                        -server   true \
                        -ssl2 0 -ssl3 0 -tls1 0 -tls1.1 0 -tls1.2 1
                    fconfigure $local -blocking 0
                } err]} {
                    dlog "TLS MITM import failed: $err"
                    set tls_ok 0
                }
                if {$tls_ok} {
                    set ::intercept_real_host($local) $tohost
                    set lhost($local) ""
                    fileevent $local readable [list read_first $log_num $local 1]
                    return
                }
                # TLS MITM failed - fall through and tunnel the raw data
            }
        }
    }

    fconfigure $local -translation binary
    if {[eof $local] || [catch {set first_part [read $local 3]}]} {
        dlog "couldn't read first part"
        cleanup_connection $local
        return
    }

    dlog "first: '$first_part'"

    if {$first_part eq ""} {
        dlog "Empty read, try again"
        fileevent $local readable ""
        catch {after cancel $::first_read_retry($local)}
        set ::first_read_retry($local) [after 50 [list retry_read_first $log_num $local $https]]
        return
    }


    if {!($first_part eq "GET" || $first_part eq "CON")} {
        if {$lhost($local) ne ""} {
            set to [split $lhost($local) :]
            if {[llength $to] == 2} {
                lassign $to tohost toport

                # Remaining :443 case (fallback if pre-read MITM failed above)
                if {$::enable_tls_interception && $toport == 443 && !$https} {
                    dlog "HTTPS CONNECT interception for $tohost:443 (fallback)"
                    # Wrap the local socket in TLS server mode (MITM)
                    set tls_ok 1
                    if {[catch {
                        fconfigure $local -translation binary -buffering none -blocking 0
                        tls::import $local \
                            -certfile $::cert_path \
                            -keyfile  $::key_path \
                            -server   true \
                            -ssl2 0 -ssl3 0 -tls1 0 -tls1.1 0 -tls1.2 1
                        fconfigure $local -blocking 0
                    } err]} {
                        dlog "TLS MITM import failed: $err"
                        set tls_ok 0
                    }

                    if {$tls_ok} {
                        # Store real tracker host so relative URL can be reconstructed
                        set ::intercept_real_host($local) $tohost
                        # Clear lhost so the relative-URL fallback below won't fire
                        set lhost($local) ""
                        # Re-arm the readable handler in HTTPS (decrypted) mode
                        fileevent $local readable [list read_first $log_num $local 1]
                        return
                    }
                    # TLS failed - fall back to plain tunnel
                }

                set ei [Event "Tunnel to peer at $lhost($local)"]
                dlog_set $ei
                set remote [route $local $tohost $toport 0]
                if {$remote ne ""} {
                    set first($remote) $first_part
                    return
                }
            }
        } elseif {$first_part eq "POS"} {
            #some clients use this for tracking and stuff
            #just kill it
            cleanup_connection $local
            return
        } else {
            dlog "Is this an https connection?"
            dlog "first_part=$first_part"

            if {$https} {
                dlog "Already tried that"
                cleanup_connection $local
                return
            }

            #this seems dumb, but we just forward everything to our https port
            set ei [Event "Intercepting https request from $linfo($local)."]
            dlog_set $ei
            set remote [route $local 127.0.0.1 $::settings(listen_port_https) $log_num]
            if {$remote ne ""} {
                set first($remote) $first_part
                return
            }

        }

        cleanup_connection $local
        return
    }

    # Now we know it is GET or CONNECT
    set ::first_line_accumulator($local) $first_part
    catch {after cancel $::first_line_timeout($local)}
    set ::first_line_timeout($local) [after 3000 [list file_line_timeout $local]]
    fconfigure $local -blocking 0
    fileevent $local readable [list read_first_line $log_num $local $https]
}

proc retry_read_first {log_num local https} {
    catch {unset ::first_read_retry($local)}
    if {[info exists ::active_local($local)]} {
        catch {fileevent $local readable [list read_first $log_num $local $https]}
    }
}

proc retry_read_remote {log_num ei local remote} {
    catch {unset ::remote_read_retry($remote)}
    if {[info exists ::rinfo($remote)] && [info exists ::active_local($local)]} {
        catch {fileevent $remote readable [list read_remote $log_num $ei $local $remote]}
    }
}

proc retry_read_local {log_num ei local remote} {
    catch {unset ::local_read_retry($local)}
    if {[info exists ::active_local($local)] && [info exists ::rinfo($remote)]} {
        catch {fileevent $local readable [list read_local $log_num $ei $local $remote]}
    }
}

proc read_first_line {log_num local https} {
    if {[eof $local]} {
        dlog "EOF while reading first line"
        cleanup_connection $local
        return
    }

    if {[catch {set data [read $local]} err]} {
        dlog "Error reading: $err"
        cleanup_connection $local
        return
    }

    if {$data eq ""} return

    append ::first_line_accumulator($local) $data
    if {[string length $::first_line_accumulator($local)] > 65536} {
        dlog "First request line exceeded 64 KiB"
        cleanup_connection $local
        return
    }

    # Look for the end of the first line (\n)
    set idx [string first "\n" $::first_line_accumulator($local)]
    if {$idx != -1} {
        fileevent $local readable ""
        set line [string range $::first_line_accumulator($local) 0 $idx]
        set extra [string range $::first_line_accumulator($local) [expr {$idx + 1}] end]
        unset ::first_line_accumulator($local)
        catch {after cancel $::first_line_timeout($local)}
        catch {unset ::first_line_timeout($local)}
        if {$extra ne ""} {
            set ::extra_data($local) $extra
        }
        set line [string trimright $line "\r\n"]
        process_first_line $log_num $local $https $line
    }
}

proc process_first_line {log_num local https line} {
    global rinfo first linfo lhost

    if {[string match "CONNECT *" $line]} {
        set verb CONNECT
    } elseif {[string match "GET *" $line]} {
        set verb GET
    } else {
        dlog "Unknown request"
        cleanup_connection $local
        return
    }


    #look for url
    set url "[lindex [split $line { }] 1]"
    dlog "Request $verb [summarize_request_target $url]"


    if {$url eq {}} {
        cleanup_connection $local
        return
    }

    if {$verb eq "CONNECT"} {
        dlog "Processing CONNECT request"
        start_connect_tunnel $log_num $local $url
        return
    }

    if {$lhost($local) ne "" && ![string match "http*" $url]} {
        set url "http://$lhost($local)$url"
    }

    # BUG FIX: reconstruct URL for intercepted HTTPS CONNECT tunnels
    # After TLS MITM, the client sends a relative URL (e.g. /announce?...)
    # We stored the real tracker host in ::intercept_real_host when we did the MITM.
    if {![string match "http*" $url] && [info exists ::intercept_real_host($local)]} {
        set real_host $::intercept_real_host($local)
        unset ::intercept_real_host($local)
        set url "https://$real_host$url"
        set https 1
    }

    if {[info exists ::extra_data($local)]} {
        set parsed_host_header [extract_host_header $::extra_data($local)]
        if {$parsed_host_header ne ""} {
            lassign $parsed_host_header host_header host_header_port
            if {$host_header ne ""} {
                set ::real_hostname($local) $host_header
            }
        }
    }

    # Unified HTTP/HTTPS URL parsing (fixes Bug 1.3 - HTTPS URLs with explicit ports)
    set parsed_url [parse_proxy_url $url $https]
    if {$parsed_url eq {}} {
        dlog_set [Event "Couldn't parse $url"]
        cleanup_connection $local
        return
    }
    lassign $parsed_url host port rest https
    dlog "Forwarding to $host:$port"


    set fake $rest
    set query_params [parse_query_params $fake]


    if {[query_dict_exists_nocase $query_params info_hash]} {

        if {[info exists ::hosts($host:$port)]} {
            incr ::hosts($host:$port)
        } else {
            set ::hosts($host:$port) 1
        }

        #Extract some query string parameters
        set types {downloaded uploaded left info_hash event}
        foreach type $types {
            set $type [query_dict_get_nocase $query_params $type]
        }

        set ::hash_lookup($log_num) $info_hash
        # Store tracker hostname per info_hash for the Torrents tab
        set ::hash_tracker($info_hash) "$host:$port"

        # Debug: log raw announce values
        dlog "ANNOUNCE VALUES: down=$downloaded up=$uploaded left=$left event=$event"

        if {$downloaded ne {} && $uploaded ne {} && $left ne {}} {
            #Have the basic tracker update parameters - mess with them.

            #This sets actual_previous_down, actual_previous_down_diff, etc
            stats_actual $info_hash $event $downloaded $uploaded $left
            set auto_seed_only [update_auto_seed_only]


            set reported_previous_down 0
            set reported_previous_up 0
            set reported_previous_left 0
            set elapsed_time 0

            if {$event ne {started}} {
                if {[info exists ::reported_last($info_hash)]} {
                        lassign $::reported_last($info_hash) reported_previous_down reported_previous_up reported_previous_left
                }
                if {[info exists ::reported_last_time($info_hash)]} {
                    if {[info exists ::env(RATIOGHOST_ISOLATED_TEST)] &&
                        $::env(RATIOGHOST_ISOLATED_TEST) eq "1" &&
                        [info exists ::env(RATIOGHOST_ISOLATED_ELAPSED_SECONDS)] &&
                        [string is integer -strict $::env(RATIOGHOST_ISOLATED_ELAPSED_SECONDS)] &&
                        $::env(RATIOGHOST_ISOLATED_ELAPSED_SECONDS) >= 0} {
                        set elapsed_time $::env(RATIOGHOST_ISOLATED_ELAPSED_SECONDS)
                    } else {
                        set elapsed_time [expr {[clock seconds] - $::reported_last_time($info_hash)}]
                    }
                }
            }


            set post "$host:$port down/up from "
            append post "[FormatData $downloaded]/[FormatData $uploaded] to "

            set last_peers 0
            if {[info exists ::response($info_hash,incomplete)]} {
                set last_peers $::response($info_hash,incomplete)
            }

            dlog "Last number of leechers was: $last_peers"

            if {[info exists ::paused] && $::paused} {
                # Paused mode - don't inflate further, but maintain consistency
                # with previously reported values to avoid anti-regression block
                dlog "PAUSED - maintaining last reported values"
                set uploaded [expr {max($uploaded, $reported_previous_up)}]
                set downloaded [expr {max($downloaded, $reported_previous_down)}]

            } elseif {$last_peers >= $::settings(min_peers)} {
                lassign [apply_download_reporting_options $fake $info_hash $event $downloaded $left] fake downloaded left

                set down_random [RatioGhostRandom]
                set up_random [RatioGhostRandom]
                set down_ratio [expr {$::settings(updown_ratio_b) + $down_random * ($::settings(updown_ratio_a) - $::settings(updown_ratio_b))}]
                set up_ratio [expr {$::settings(upup_ratio_b) + $up_random * ($::settings(upup_ratio_a) - $::settings(upup_ratio_b))}]

                dlog "Previous upload was: $reported_previous_up"
                dlog "Actual download was: $actual_previous_down_diff"
                dlog "Actual upload was: $actual_previous_up_diff"
                dlog "Random download ratio is: $down_ratio"
                dlog "Random upload ratio is: $up_ratio"

                set uploaded [expr {$reported_previous_up + $actual_previous_up_diff}]
                set uploaded [expr {$uploaded + ($down_ratio * $actual_previous_down_diff)}]
                set uploaded [expr {$uploaded + ($up_ratio * $actual_previous_up_diff)}]

                dlog "Time from last report was: $elapsed_time"
                dlog "Rolling for boost."

                set boost_chance_random [RatioGhostRandom]
                if {$boost_chance_random * 100 < $::settings(boost_chance)} {
                    set boost_random [RatioGhostRandom]
                    set boost [expr {$::settings(boost) * 1024 * $elapsed_time * $boost_random}]
                    dlog "Adding in extra boost of: $boost"
                    set uploaded [expr {$uploaded + $boost}]
                }


            } else {
                lassign [apply_download_reporting_options $fake $info_hash $event $downloaded $left] fake downloaded left

                dlog "Didn't meet peer count - setting in actual upload."
                dlog "Actual upload was: $actual_previous_up_diff"
                set uploaded [expr {$reported_previous_up + $actual_previous_up_diff}]

            }

            dlog "Setting uploaded to $uploaded"


            if {$event ne {started}} {
                if {$uploaded < $reported_previous_up} {
                    dlog_set [Event "($host) LOGIC ERROR - SKIPPING TO AVOID DETECTION"]
                    foreach e [list actual_previous_down actual_previous_up actual_previous_down_diff actual_previous_up_diff \
                        reported_previous_down reported_previous_up reported_previous_left uploaded downloaded left] {
                            dlog "DEBUG $e [set $e]"
                        }
                        cleanup_connection $local
                        return
                }
            }

            set uploaded [format %.0f $uploaded]

            #splice back in uploaded, downloaded, left
            set query_updates {}
            foreach type [lrange $types 0 2] {
                dict set query_updates $type [set $type]
            }
            set fake [rewrite_query_params $fake $query_updates {}]

            append post "[FormatData $downloaded]/[FormatData $uploaded]"

            if {$event ne {}} {
                append post " ($event)"
            }

            dlog $post
            set ei [Event $post]
            dlog "Event idx $ei"
            dlog_set $ei

            stats_reported $info_hash $event $downloaded $uploaded $left
            dlog "Changed announce request statistics"

        } else {
            #Has infohash, but not downloaded, uploaded, etc
            #Probably scrape, which we don't really care about.
            dlog "Forwarding non-announce traffic."
            set ei [Event "$host:$port Non-announce traffic."]
            dlog_set $ei
        }


    } else {
        #No infohash
        if {[request_must_be_tracker $local]} {
            dlog "Blocking non-tracker traffic."
            set ei [Event "$host:$port Blocked non-tracker traffic."]
            dlog_set $ei
            cleanup_connection $local
            return
        } else {
            dlog "Forwarding non-tracker traffic."
            set ei [Event "$host:$port Forwarding non-tracker traffic."]
            dlog_set $ei
        }
    }



    set remote [route $local $host $port $log_num $https]
    if {$remote ne ""} {
        set first($remote) "GET $fake HTTP/1.1\r\n"
    }

}


# Timeout handler for remote socket connections
proc socket_timeout {remote local {ei ""}} {
    if {[info exists ::rinfo($remote)]} {
        dlog "Connection timed out - $local <-> $remote - $::linfo($local) <-> $::rinfo($remote)"
    } else {
        dlog "Connection timed out - $local <-> $remote"
    }
    if {$ei ne ""} {
        EventAppend $ei " (timeout)"
    }
    cleanup_connection $local $remote
}

proc connect_handler {log_num ei local remote host https} {
    global rinfo linfo

    # Clear the writable handler so it doesn't fire again
    fileevent $remote writable ""

    # Check for connection error
    set err [fconfigure $remote -error]
    if {$err ne ""} {
        dlog "Async connection to $host failed: $err"
        EventAppend $ei " (error: $err)"
        cleanup_connection $local $remote
        return
    }

    arm_socket_timeout $remote $local $ei

    dlog "Async connection to $host established"

    if {$https} {
        dlog "Setting to HTTPS on connected socket"
        # Prefer the decrypted Host header so both SNI and certificate identity
        # validation use the tracker name rather than a resolved IP address.
        set expected_host $host
        if {[info exists ::real_hostname($local)]} {
            set expected_host $::real_hostname($local)
        }
        if {[catch {set import_opts [TlsClientOptions $expected_host]} tls_config_error]} {
            dlog "Secure TLS configuration unavailable: $tls_config_error"
            EventAppend $ei " (trusted CA unavailable)"
            cleanup_connection $local $remote
            return
        }
        dlog "Validating TLS peer: $expected_host"
        if {[catch {
            tls::import $remote {*}$import_opts
        } import_err]} {
            dlog "TLS import on connected socket failed: $import_err"
            EventAppend $ei " (TLS error)"
            cleanup_connection $local $remote
            return
        }
    }

    # Configure encoding, translation and setup event handlers
    fconfigure $remote -buffering none -blocking 0 -encoding binary -translation binary
    fileevent $remote readable [list read_remote $log_num $ei $local $remote]

    fconfigure $local -buffering none -blocking 0 -encoding binary -translation binary
    fileevent $local readable [list read_local $log_num $ei $local $remote]

    # Trigger sending of buffered data (first line and extra headers)
    read_local $log_num $ei $local $remote
}

proc tunnel_connect_handler {log_num ei local remote host payload} {
    fileevent $remote writable ""

    set err [fconfigure $remote -error]
    if {$err ne ""} {
        dlog "Async tunnel connection to $host failed: $err"
        catch {
            puts -nonewline $local "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"
            flush $local
        }
        EventAppend $ei " (error: $err)"
        cleanup_connection $local $remote
        return
    }

    arm_socket_timeout $remote $local $ei
    fconfigure $remote -buffering none -blocking 0 -encoding binary -translation binary
    fconfigure $local -buffering none -blocking 0 -encoding binary -translation binary
    fileevent $remote readable [list read_remote $log_num $ei $local $remote]
    fileevent $local readable [list read_local $log_num $ei $local $remote]

    if {$payload ne ""} {
        set ::first($remote) $payload
    }
    puts -nonewline $local "HTTP/1.1 200 Connection Established\r\n\r\n"
    flush $local

    # Trigger sending of buffered data (any pipelined payload from CONNECT)
    read_local $log_num $ei $local $remote
}

proc route_tunnel {local host port log_num ei payload} {
    global rinfo

    dlog "Opening tunnel socket $host:$port"
    if {[catch {set remote [socket -async $host $port]} err]} {
        dlog "Couldn't open tunnel socket to remote host: $err"
        EventAppend $ei " (error)"
        cleanup_connection $local
        return ""
    }

    set ::local_remote($local) $remote
    set ::remote_local($remote) $local
    set ::socket_timeout($remote) [after 15000 [list socket_timeout $remote $local $ei]]
    set rinfo($remote) [list $host $port]

    fconfigure $remote -buffering none -blocking 0
    fileevent $remote writable [list tunnel_connect_handler $log_num $ei $local $remote $host $payload]
    return $remote
}

proc route {local host port log_num {https 0}} {
    upvar 1 ei ei
    global rinfo linfo

    dlog "Opening socket $host:$port"
    set err {}
    set e [catch {set remote [socket -async $host $port]} err]
    if {$e} {
        dlog "Couldn't open socket to remote host: $err"
        EventAppend $ei " (error)"
        cleanup_connection $local
        return ""
    }

    set ::local_remote($local) $remote
    set ::remote_local($remote) $local
    set ::socket_timeout($remote) [after 30000 [list socket_timeout $remote $local $ei]]
    set rinfo($remote) [list $host $port]

    fconfigure $remote -buffering none -blocking 0
    fileevent $remote writable [list connect_handler $log_num $ei $local $remote $host $https]

    return $remote
}

proc extract_tracker_response_fields {data} {
    set result {}
    foreach field {complete incomplete interval} {
        set pattern [format {%d:%si([0-9]+)e} [string length $field] $field]
        if {[regexp $pattern $data _ value]} {
            dict set result $field $value
        }
    }

    if {[regexp {14:failure reason([0-9]{1,5}):} $data _ length] && $length <= 4096} {
        set marker "14:failure reason$length:"
        set start [string first $marker $data]
        if {$start >= 0} {
            incr start [string length $marker]
            set end [expr {$start + $length - 1}]
            if {$end < [string length $data]} {
                dict set result failure_reason [string range $data $start $end]
            }
        }
    }
    return $result
}



proc read_remote {log_num ei local remote} {
    global rinfo linfo

    arm_socket_timeout $remote $local $ei

    if {[eof $remote] || [catch {set l [read $remote]}]} {
        dlog "Closed remote - $local <-> $remote - $linfo($local) <-> $rinfo($remote)"
        cleanup_connection $local $remote
        return
    }
    if {$l eq {}} {
        fileevent $remote readable ""
        catch {after cancel $::remote_read_retry($remote)}
        set ::remote_read_retry($remote) [after 50 [list retry_read_remote $log_num $ei $local $remote]]
        return
    }
    dlog "Sending [string length $l] bytes - $local <- $remote - $linfo($local) <- $rinfo($remote)"

    catch {puts -nonewline $local $l}
    catch {flush $local}
    dlog "Received [string length $l] bytes from $rinfo($remote)"


    if {[info exists ::hash_lookup($log_num)]} {
        append ::response_accumulator($remote) $l
        if {[string length $::response_accumulator($remote)] > 1048576} {
            dlog "Tracker response exceeded the 1 MiB parsing limit"
            unset ::response_accumulator($remote)
            return
        }

        set hash $::hash_lookup($log_num)
        set parsed [extract_tracker_response_fields $::response_accumulator($remote)]
        foreach field {complete incomplete interval} {
            if {[dict exists $parsed $field]} {
                set ::response($hash,$field) [dict get $parsed $field]
            }
        }

        set summary ""
        if {[dict exists $parsed failure_reason]} {
            set failure_reason [dict get $parsed failure_reason]
            set summary " (fail: $failure_reason)"
        } elseif {[dict exists $parsed complete] || [dict exists $parsed incomplete] || [dict exists $parsed interval]} {
            set complete [expr {[info exists ::response($hash,complete)] ? $::response($hash,complete) : 0}]
            set incomplete [expr {[info exists ::response($hash,incomplete)] ? $::response($hash,incomplete) : 0}]
            set interval [expr {[info exists ::response($hash,interval)] ? $::response($hash,interval) : 0}]
            set summary " ($complete/$incomplete, [FormatElapsed $interval])"
        }

        if {$summary ne "" && (![info exists ::response_event_summary($remote)] || $::response_event_summary($remote) ne $summary)} {
            set ::response_event_summary($remote) $summary
            dlog "Tracker response summary updated"
            EventAppend $ei $summary
        }
    }

}


proc read_local {log_num ei local remote} {
    global rinfo linfo first
    arm_socket_timeout $remote $local $ei
    if {[eof $local] || [catch {set l [read $local]}]} {
        dlog "Closed local - $local <-> $remote - $linfo($local) <-> $rinfo($remote)"
        cleanup_connection $local $remote
        return
    }
    if {$l eq {} && ![info exists ::extra_data($local)] && ![info exists first($remote)]} {
        fileevent $local readable ""
        catch {after cancel $::local_read_retry($local)}
        set ::local_read_retry($local) [after 50 [list retry_read_local $log_num $ei $local $remote]]
        return
    }
    if {[info exists ::extra_data($local)]} {
        set l "$::extra_data($local)$l"
        unset ::extra_data($local)
    }
    if {[info exists first($remote)]} {
        set l "$first($remote)$l"
        unset first($remote)
    }

    #Some programs put 127.0.0.1 in the Host - can't have that.
    lassign $rinfo($remote) host port
    set host_header [format_host_header $host $port]
    set l [rewrite_loopback_host_headers $l $host_header $::settings(listen_port) $::settings(listen_port_https)]


    #set ll [open $local.txt a]
    #fconfigure $ll -translation binary
    #puts -nonewline $ll $l
    #close $ll

    dlog "Sending [string length $l] bytes - $local -> $remote - $linfo($local) -> $rinfo($remote)"
    catch {puts -nonewline $remote $l}
    catch {flush $remote}
    dlog "Sent [string length $l] bytes to $rinfo($remote)"
}


proc listener_ports_overlap {current requested} {
    foreach old_port [lrange $current 0 1] {
        if {$old_port in [lrange $requested 0 1]} {return 1}
    }
    return 0
}

proc open_listener_pair {http_port https_port only_local} {
    set socket_options {}
    if {$only_local} {
        set socket_options [list -myaddr 127.0.0.1]
    }

    set http_socket ""
    set https_socket ""
    try {
        set http_socket [socket -server prox {*}$socket_options $http_port]
        set https_socket [socket -server prox_https {*}$socket_options $https_port]
    } on error {message options} {
        if {$http_socket ne ""} {catch {close $http_socket}}
        if {$https_socket ne ""} {catch {close $https_socket}}
        return -options $options $message
    }
    return [list $http_socket $https_socket]
}

proc restore_listener_settings {configuration} {
    set ::listen_reconfiguring 1
    try {
        set ::settings(listen_port) [lindex $configuration 0]
        set ::settings(listen_port_https) [lindex $configuration 1]
        set ::settings(only_local) [lindex $configuration 2]
    } finally {
        set ::listen_reconfiguring 0
    }
}

proc listen args {
    global listen_socket listen_socket_https

    if {$::settings(listen_port) eq {}} return
    if {![string is integer -strict $::settings(listen_port)]} return
    if {$::settings(listen_port) < 1 || $::settings(listen_port) > 65534} {
        return -code error "Listen port must be between 1 and 65534 because Ratio Ghost also reserves the adjacent HTTPS tunnel port."
    }

    set ::settings(listen_port_https) [expr {$::settings(listen_port) + 1}]
    set requested [list $::settings(listen_port) $::settings(listen_port_https) $::settings(only_local)]
    if {[info exists ::listen_current] && $::listen_current eq $requested &&
        [info exists listen_socket] && $listen_socket ne ""} {
        return
    }

    set previous {}
    if {[info exists ::listen_current]} {
        set previous $::listen_current
    }
    set must_close_first [expr {$previous ne "" && [listener_ports_overlap $previous $requested]}]

    if {$must_close_first} {
        catch {close $listen_socket}
        catch {close $listen_socket_https}
        set listen_socket ""
        set listen_socket_https ""
    }

    if {[catch {
        lassign [open_listener_pair {*}$requested] new_listen_socket new_listen_socket_https
    } err]} {
        set restore_error ""
        if {$previous ne ""} {
            if {$must_close_first} {
                if {[catch {
                    lassign [open_listener_pair {*}$previous] listen_socket listen_socket_https
                } restore_error]} {
                    set listen_socket ""
                    set listen_socket_https ""
                }
            }
            set ::listen_current $previous
            restore_listener_settings $previous
        }
        set message "Could not listen on ports [lindex $requested 0]/[lindex $requested 1]: $err"
        if {$restore_error ne ""} {
            append message "; restoring the previous listener also failed: $restore_error"
        }
        if {[info commands Event] ne ""} {Event $message}
        return -code error $message
    }

    if {!$must_close_first && [info exists listen_socket] && $listen_socket ne ""} {
        catch {close $listen_socket}
        catch {close $listen_socket_https}
    }
    set listen_socket $new_listen_socket
    set listen_socket_https $new_listen_socket_https
    set ::listen_current $requested

    puts "Listening with $listen_socket on $::settings(listen_port)"
    set listen_address [expr {$::settings(only_local) ? "127.0.0.1" : "all interfaces"}]
    Event "Listening on $listen_address:$::settings(listen_port) and $listen_address:$::settings(listen_port_https) (CONNECT tunnel compatibility)"
}
