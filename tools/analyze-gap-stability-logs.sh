#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then echo "Usage: $0 <trade-log.log> [more-log-files...]" >&2; exit 2; fi

awk '
function val(key, p,rest) { p="(^|[[:space:]])" key "="; if(match($0,p)){rest=substr($0,RSTART+RLENGTH);if(match(rest,/^[^[:space:]]+/))return substr(rest,RSTART,RLENGTH)} return "-" }
function qval(key, p,rest) { p="(^|[[:space:]])" key "=\""; if(match($0,p)){rest=substr($0,RSTART+RLENGTH);if(match(rest,/^[^\"]*/))return substr(rest,RSTART,RLENGTH)} return "" }
function add(g,n,v) { total[g SUBSEP n]+=v }
function setpolicy(g,n,v) { if(v!="-")policy[g,n]=v }
function grp(a,s,slot,cfg,sym) { return a SUBSEP s SUBSEP slot SUBSEP cfg SUBSEP sym }
function absn(n) { return n<0 ? -n : n }
function gapband(g,n, m,b) { m=absn(n+0); if(m<=5)b="gap_0_5";else if(m<=10)b="gap_6_10";else if(m<=20)b="gap_11_20";else if(m<=50)b="gap_21_50";else if(m<=100)b="gap_51_100";else if(m<=200)b="gap_101_200";else if(m<=500)b="gap_201_500";else b="gap_501_plus"; add(g,b,1);add(g,"observed_gaps",1) }
function sampleband(g,n,b) { if(n<=2)b="samples_1_2";else if(n<=5)b="samples_3_5";else if(n<=10)b="samples_6_10";else if(n<=30)b="samples_11_30";else if(n<=100)b="samples_31_100";else b="samples_101_plus";add(g,b,1) }
function durationband(g,n,b) { if(n<250)b="duration_under_250";else if(n<1000)b="duration_250_999";else if(n<2000)b="duration_1000_1999";else if(n<5000)b="duration_2000_4999";else b="duration_5000_plus";add(g,b,1) }
function dispersionband(g,n,b) { if(n<=.10)b="dispersion_0_010";else if(n<=.20)b="dispersion_010_020";else if(n<=.35)b="dispersion_020_035";else if(n<=.45)b="dispersion_035_045";else b="dispersion_over_045";add(g,b,1) }
function driftband(g,n,b) { if(n<=.10)b="drift_0_010";else if(n<=.20)b="drift_010_020";else if(n<=.40)b="drift_020_040";else if(n<=.60)b="drift_040_060";else b="drift_over_060";add(g,b,1) }
function printset(g,title,names,count, i,n) { print title; for(i=1;i<=count;i++){n=names[i];print n "," total[g SUBSEP n]+0} }

/\[GAP_STABILITY_RAW\]\[CONFIG\]/ {
 a=val("action");s=val("side");cfg=val("config_id");sym=val("symbol");g=grp(a,s,"-",cfg,sym)
 setpolicy(g,"absolute_floor",val("absolute_floor"));setpolicy(g,"relative_tolerance",val("relative_tolerance"));setpolicy(g,"mad_multiplier",val("mad_multiplier"));setpolicy(g,"min_stable_samples",val("min_stable_samples"));setpolicy(g,"max_dispersion",val("max_dispersion"));setpolicy(g,"max_drift",val("max_drift"));setpolicy(g,"hold_confirm_ms",val("hold_confirm_ms"));setpolicy(g,"limit_max_gap",val("limit_max_gap"));setpolicy(g,"max_gap",val("max_gap"))
 next
}

/\[(GAP_STABILITY|GAP_STABILITY_RAW)\]\[(CYCLE_STABLE|CYCLE_COMPLETED|TRIGGER_EMITTED)\]/ {
 a=val("action");s=val("side");slot=val("slot_id");cfg=val("config_id");sym=val("symbol");cid=val("cycle_id");g=grp(a,s,slot,cfg,sym);groups[g]=1
 if(cid!="-"){cycle_group[cid]=g;cycle_gaps[cid]=qval("gaps");cycle_samples[cid]=val("total_sample_count");cycle_duration[cid]=val("duration_ms");cycle_dispersion[cid]=val("dispersion");cycle_drift[cid]=val("drift");if(!(cid in cycle_seen)){cycle_seen[cid]=1;add(g,"cycles",1)}}
 setpolicy(g,"absolute_floor",val("absolute_floor"));setpolicy(g,"relative_tolerance",val("relative_tolerance"));setpolicy(g,"mad_multiplier",val("mad_multiplier"));setpolicy(g,"min_stable_samples",val("min_stable_samples"));setpolicy(g,"max_dispersion",val("max_dispersion"));setpolicy(g,"max_drift",val("max_drift"));setpolicy(g,"hold_confirm_ms",val("hold_confirm_ms"));setpolicy(g,"limit_max_gap",val("limit_max_gap"));setpolicy(g,"max_gap",val("max_gap"))
 if($0~/\[CYCLE_STABLE\]/ && !(cid in stable_seen)){stable_seen[cid]=1;add(g,"stable",1);d=val("duration_ms");if(d!="-"){add(g,"stable_duration_total",d+0);add(g,"stable_duration_count",1)}}
 if($0~/\[CYCLE_COMPLETED\]/){r=val("result");if(r=="UNSTABLE")add(g,"unstable",1);else if(r=="REJECTED")add(g,"rejected",1);else if(r=="DELTA_SPLIT")add(g,"delta_split",1);else if(r=="RESET_CONFIRM")add(g,"reset_confirm",1);else if(r=="RESET_MISSING_DATA")add(g,"reset_missing",1);else if(r=="RESET_TIMESTAMP")add(g,"reset_timestamp",1);else if(r=="RESET_EXPLICIT")add(g,"reset_explicit",1);if($0~/Dispersion/)add(g,"unstable_dispersion_reason",1);if($0~/Drift/)add(g,"unstable_drift_reason",1)}
 if($0~/\[TRIGGER_EMITTED\]/){sig=val("signal_id");add(g,"triggers",1);if(sig!="-")signal_group[sig]=g}
}
/\[GAP_STABILITY\]\[GUARD\]/ { sig=val("signal_id");g=signal_group[sig];if(g=="")next;r=val("result");if(r=="PASS")add(g,"guard_pass",1);else if(r=="BLOCKED")add(g,"guard_blocked",1) }
/\[GAP_STABILITY\]\[OUTCOME\]/ { sig=val("signal_id");g=signal_group[sig];if(g=="")next;r=val("outcome");if(r=="CONFIRMED")add(g,"confirmed",1);else if(r=="BLOCKED")add(g,"blocked",1);else if(r=="FAILED")add(g,"failed",1);else if(r=="CANCELLED")add(g,"cancelled",1) }

END {
 for(cid in cycle_group){g=cycle_group[cid];n=cycle_samples[cid];d=cycle_duration[cid];p=cycle_dispersion[cid];r=cycle_drift[cid];if(n!="-")sampleband(g,n+0);if(d!="-"){durationband(g,d+0);add(g,"duration_total",d+0);add(g,"duration_count",1)}if(p!="-")dispersionband(g,p+0);if(r!="-")driftband(g,r+0);count=split(cycle_gaps[cid],gv,/[|]/);for(i=1;i<=count;i++)if(gv[i]!="")gapband(g,gv[i])}
 print "Gap Stability analysis"
 for(g in groups){split(g,part,SUBSEP);print "";print "group";print "action," part[1];print "side," part[2];print "slot_id," part[3];print "config_id," part[4];print "symbol," part[5]
  print "policy";pn[1]="absolute_floor";pn[2]="relative_tolerance";pn[3]="mad_multiplier";pn[4]="min_stable_samples";pn[5]="max_dispersion";pn[6]="max_drift";pn[7]="hold_confirm_ms";pn[8]="limit_max_gap";pn[9]="max_gap";for(i=1;i<=9;i++){n=pn[i];print n "," policy[g,n]}
  rn[1]="cycles";rn[2]="stable";rn[3]="unstable";rn[4]="rejected";rn[5]="delta_split";rn[6]="reset_confirm";rn[7]="reset_missing";rn[8]="reset_timestamp";rn[9]="reset_explicit";rn[10]="unstable_dispersion_reason";rn[11]="unstable_drift_reason";printset(g,"cycle_results",rn,11)
  sc=total[g SUBSEP "stable_duration_count"];dc=total[g SUBSEP "duration_count"];print "avg_stable_duration_ms," (sc?sprintf("%.2f",total[g SUBSEP "stable_duration_total"]/sc):"0.00");print "avg_cycle_duration_ms," (dc?sprintf("%.2f",total[g SUBSEP "duration_total"]/dc):"0.00")
  sn[1]="triggers";sn[2]="guard_pass";sn[3]="guard_blocked";sn[4]="confirmed";sn[5]="blocked";sn[6]="failed";sn[7]="cancelled";printset(g,"signal_results",sn,7)
  gn[1]="observed_gaps";gn[2]="gap_0_5";gn[3]="gap_6_10";gn[4]="gap_11_20";gn[5]="gap_21_50";gn[6]="gap_51_100";gn[7]="gap_101_200";gn[8]="gap_201_500";gn[9]="gap_501_plus";printset(g,"gap_distribution",gn,9)
  smn[1]="samples_1_2";smn[2]="samples_3_5";smn[3]="samples_6_10";smn[4]="samples_11_30";smn[5]="samples_31_100";smn[6]="samples_101_plus";printset(g,"sample_distribution",smn,6)
  dn[1]="duration_under_250";dn[2]="duration_250_999";dn[3]="duration_1000_1999";dn[4]="duration_2000_4999";dn[5]="duration_5000_plus";printset(g,"duration_distribution_ms",dn,5)
  dpn[1]="dispersion_0_010";dpn[2]="dispersion_010_020";dpn[3]="dispersion_020_035";dpn[4]="dispersion_035_045";dpn[5]="dispersion_over_045";printset(g,"dispersion_distribution",dpn,5)
  drn[1]="drift_0_010";drn[2]="drift_010_020";drn[3]="drift_020_040";drn[4]="drift_040_060";drn[5]="drift_over_060";printset(g,"drift_distribution",drn,5)
 }
}
' "$@"
