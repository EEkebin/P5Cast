cbuffer OfflineScene : register(b2) { float demoTime, demoSteer, demoDistance, demoWhite; };

float DemoRidge(float x) {
    return sin(x*8+.4)*.024 + sin(x*19+1.9)*.017 + sin(x*41)*.008 + sin(x*99)*.002;
}
float DemoLine(float value,float width) {
    return 1-smoothstep(width,width+max(.0005,fwidth(value)),abs(value));
}
float4 DemoScene(Vertex v) : SV_Target {
    float2 uv=v.uv;
    float2 p=float2((uv.x-.5)*1.7778,uv.y);
    float h=.44, sky=saturate(uv.y/h),travel=demoDistance;
    float3 col=lerp(float3(.044,.087,.17),float3(.91,.61,.52),pow(sky,1.35));
    float sun=length(float2(p.x-.37,p.y-.28));
    col+=float3(.21,.078,.008)*exp(-sun*9);
    col=lerp(col,float3(1,.815,.57),1-smoothstep(.078,.080,sun));
    float ridge=h-.066+DemoRidge(p.x+sin(travel*.13)*.027);
    col=lerp(col,float3(.32,.32,.45),smoothstep(ridge-.001,ridge+.001,p.y));
    ridge=h-.021+DemoRidge(p.x*1.4+sin(travel*.16)*.045);
    col=lerp(col,float3(.17,.225,.316),smoothstep(ridge-.001,ridge+.001,p.y));
    if(p.y>=h) {
        float depth=(p.y-h)/(1-h),safeDepth=max(.012,depth);
        float bend=.085*sin(travel*.24+1.9*(1-depth))*(1-depth);
        float x=(p.x-bend)/safeDepth;
        float road=1-smoothstep(.70,.71,abs(x));
        col=lerp(lerp(float3(.29,.35,.39),float3(.064,.14,.15),sqrt(depth)),
                 lerp(float3(.31,.34,.39),float3(.065,.091,.12),sqrt(depth)),road);
        float longitudinal=1.0/safeDepth+travel*.83;
        float stripe=step(.48,frac(longitudinal*.94))*smoothstep(.035,.25,depth);
        col=lerp(col,float3(.74,.88,.83),stripe*DemoLine(abs(x)-.235,.0035));
        col=lerp(col,float3(.24,.89,.78),DemoLine(abs(x)-.704,.008)*smoothstep(.015,.16,depth));
    }
    // Guide gates accelerate toward the camera through perspective division.
    [unroll] for(int i=3;i>=0;i--) {
        float z=1/(1.8+((float)i+1-frac(travel*.39))*3.8);
        float lane=sin((floor(travel*.39)+i+1)*1.92)*.40;
        float2 local=p-float2(z*lane+.045*(1-z),h+z*.66);
        float2 box=abs(local-float2(0,-z*.13))-float2(z*.21,z*.13);
        float outline=abs(max(box.x,box.y));
        float width=.0012+z*.006;
        if(local.y<=0) col=lerp(col,float3(.50,.97,.84),(1-smoothstep(width,width+.0012,outline))*min(1,z*5));
    }
    float2 ship=float2(p.x-demoSteer*.48,p.y-.81);
    float body=abs(ship.x)-.065*saturate((ship.y+.053)/.074);
    if(ship.y>=-.053 && ship.y<=.021 && body<0)
        col=lerp(float3(.76,.94,.88),float3(.19,.52,.57),smoothstep(.008,.042,abs(ship.x)));
    if(ship.y>.021 && ship.y<.077 && abs(ship.x)<(.077-ship.y)*(.31+.055*sin(demoTime*31)))
        col=lerp(float3(1,.73,.38),float3(.87,.32,.17),saturate((ship.y-.021)*18));
    if(demoWhite>0) col=Direct709ToLinear(col)*demoWhite;
    return float4(col,1);
}
