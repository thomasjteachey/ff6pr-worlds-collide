import sys; sys.path.insert(0,'tools')
import ff6snes as S, prdata
INFO, GFX, PAL = 0x127000, 0x297000, 0x127820
def parts(r,i):
    e=r.b(INFO+5*i,5); ptr=e[0]|e[1]<<8; bpp3=bool(ptr&0x8000)
    pal=(e[2]&0x03)<<8|e[3]
    return bytes(r.b(GFX+(ptr&0x7FFF)*8,96)), bytes(r.b(PAL+16*pal, 16 if bpp3 else 32)), e[4], bool(e[2]&0x80)
b=S.Rom(r'C:\Projects\Gamedev\ff6-mod\build\brave-new-world\Brave New World 3.0 FRC.sfc'); v=S.Rom(prdata.VANILLA_ROM)
be={e.id:e for e in S.enemies(b)}; ve={e.id:e for e in S.enemies(v)}
V=[parts(v,i) for i in range(384)]; B=[parts(b,i) for i in range(384)]
same=0; swap=[]; recolor=[]; new=[]
for i in range(384):
    if B[i]==V[i]: same+=1; continue
    js=[j for j in range(384) if V[j]==B[i]]
    if js: swap.append((i,be[i].name.strip(),js[0],ve[js[0]].name.strip())); continue
    js=[j for j in range(384) if V[j][0]==B[i][0]]
    if js: recolor.append((i,be[i].name.strip(),js[0],ve[js[0]].name.strip())); continue
    new.append((i,be[i].name.strip(),ve[i].name.strip()))
print('same',same,'another vanilla sprite',len(swap),'recolor of a vanilla sprite',len(recolor),'no match',len(new))
print('SWAP',swap[:40]); print('RECOLOR',recolor[:40]); print('NEW',new[:40])
