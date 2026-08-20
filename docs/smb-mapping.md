# Verified SMB Mapping

Collected on 2026-08-20 through the existing SSH aliases. No SSH or SMB configuration was changed.

## PC-A: IRIS0FTHEVALLEY

| Local drive | Remote path |
|---|---|
| `R:` | `\\192.168.1.7\ID-BLUEBERRY_C` |
| `S:` | `\\192.168.1.7\ID-BLUEBERRY_D` |
| `T:` | `\\192.168.1.7\ID-BLUEBERRY_E` |

PC-A publishes `IRIS0FTHEVALLEY_C`, `_D`, `_F`, `_G`, `_H`, `_I`, and `_J` for peers.

## PC-B: ID-BLUEBERRY

PC-B publishes `ID-BLUEBERRY_C`, `_D`, and `_E`. No persistent reverse mapped drive was present. Direct UNC access from PC-B to PC-A was tested and returned `UnauthorizedAccessException`; reverse live transfer remains blocked until PC-A permits the authenticated PC-B account on the relevant share/NTFS ACL.

The resolver uses UNC paths and does not assume that the peer has the same drive letters.
