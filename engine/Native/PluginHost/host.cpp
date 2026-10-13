// Copyright (c) Graze contributors. VST3 SDK is used under its MIT license.
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "public.sdk/source/common/memorystream.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
using namespace Steinberg;
using namespace Steinberg::Vst;
namespace {
constexpr uint32_t Magic = 0x31485047, MaxMessage = 16 * 1024 * 1024;
int replyFd;
struct Bytes {
    std::vector<uint8_t> data; size_t cursor = 0;
    template<class T> T read() { if (cursor + sizeof(T) > data.size()) throw std::runtime_error("Truncated request"); T v; std::memcpy(&v, data.data()+cursor, sizeof(T)); cursor += sizeof(T); return v; }
    template<class T> void put(T v) { auto p = reinterpret_cast<const uint8_t*>(&v); data.insert(data.end(), p, p+sizeof(T)); }
    std::vector<uint8_t> blob() { auto n = read<uint32_t>(); if (n > MaxMessage || cursor+n > data.size()) throw std::runtime_error("Invalid blob"); std::vector<uint8_t> v(data.begin()+cursor, data.begin()+cursor+n); cursor += n; return v; }
    std::string str() { auto b = blob(); return std::string(b.begin(), b.end()); }
    void blob(const void* p, size_t n) { if(n > MaxMessage) throw std::runtime_error("State too large"); put<uint32_t>(static_cast<uint32_t>(n)); if(n) { auto b = static_cast<const uint8_t*>(p); data.insert(data.end(),b,b+n); } }
    void str(const std::string& s) { blob(s.data(),s.size()); }
    void end() { if (cursor != data.size()) throw std::runtime_error("Trailing request data"); }
};
bool receive(void* p, size_t n) { auto b=static_cast<char*>(p); while(n) { int k=_read(0,b,static_cast<unsigned>(std::min<size_t>(n,65536))); if(k<=0)return false; b+=k;n-=k; } return true; }
void send(const void* p,size_t n) { auto b=static_cast<const char*>(p);while(n){int k=_write(replyFd,b,static_cast<unsigned>(std::min<size_t>(n,65536)));if(k<=0)throw std::runtime_error("Broken reply pipe");b+=k;n-=k;} }
std::wstring wide(const std::string& s) { int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),nullptr,0); if(!n)throw std::runtime_error("Invalid UTF-8 path");std::wstring w(n,0);MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),&w[0],n);return w; }
std::string text(const TChar* s) { return StringConvert::convert(s); }
void pump() { MSG m;while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);} }
struct Processor {
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider; IPtr<IComponent> component; IPtr<IEditController> controller; IPtr<IAudioProcessor> processor;
    HostProcessData audio; EventList events{65536}; ParameterChanges changes{4096}; ProcessContext context{};
    HANDLE mapping=nullptr; void* view=nullptr; float* pcm=nullptr; float* inputPcm=nullptr; bool effect=false;
    bool multi=false; std::vector<int32> selected; std::vector<BusInfo> outputs;
    std::vector<std::pair<ParamID,double>> pending;
    struct AutomationPoint { ParamID id; int32 offset; double value; };
    std::vector<AutomationPoint> automation; // op 8: sample-accurate parameter points for the next block (sorted by id, then offset)
    int sampleRate=48000,maxFrames=1024; bool running=false; int64_t produced=0;
    void close() {
        if(processor && running)processor->setProcessing(false);
        if(component && running)component->setActive(false);
        running=false; audio.unprepare(); processor=nullptr;controller=nullptr;component=nullptr;provider=nullptr;module.reset();
        if(view)UnmapViewOfFile(view);if(mapping)CloseHandle(mapping);view=nullptr;mapping=nullptr;pcm=nullptr;inputPcm=nullptr;pending.clear();selected.clear();outputs.clear();multi=false;
    }
    ~Processor(){close();}
    void load(Bytes& r,Bytes& out,bool isEffect=false,bool isMulti=false) {
        close(); effect=isEffect;multi=isMulti; auto path=r.str(),id=r.str(),mapName=r.str();sampleRate=r.read<int32_t>();maxFrames=r.read<int32_t>();
        if(multi){int n=r.read<int32_t>();if(n<1||n>16)throw std::runtime_error("Select 1 to 16 outputs");for(int i=0;i<n;i++){int bus=r.read<int32_t>();if(bus<0||bus>=64||std::find(selected.begin(),selected.end(),bus)!=selected.end())throw std::runtime_error("Invalid/duplicate output bus");selected.push_back(bus);}}r.end();
        if(sampleRate<8000||sampleRate>192000||maxFrames<16||maxFrames>4096)throw std::runtime_error("Invalid audio configuration");
        std::string error;module=VST3::Hosting::Module::create(path,error);if(!module)throw std::runtime_error(error);
        const auto& factory=module->getFactory();factory.setHostContext(PluginContextFactory::instance().getPluginContext());
        bool found=false;
        for(const auto& c:factory.classInfos())if(c.ID().toString()==id && c.category()==kVstAudioEffectClass) {
            bool instrument=c.subCategoriesString().find("Instrument")!=std::string::npos;
            if(instrument==effect)throw std::runtime_error("Plugin class does not match the requested instrument/effect kind");
            provider=owned(new PlugProvider(factory,c,true));if(!provider->initialize())throw std::runtime_error("VST3 initialization failed");
            component=provider->getComponentPtr();controller=provider->getControllerPtr();processor=U::cast<IAudioProcessor>(component);
            if(!component||!processor||!controller||processor->canProcessSampleSize(kSample32)!=kResultOk)throw std::runtime_error("Float32 instrument processing is unavailable");
            BusInfo bus{};if(component->getBusInfo(kAudio,kOutput,0,bus)!=kResultOk||bus.channelCount<1||bus.channelCount>2)throw std::runtime_error("Instrument main output must be mono or stereo");
            if(effect&&(component->getBusInfo(kAudio,kInput,0,bus)!=kResultOk||bus.channelCount<1||bus.channelCount>2))throw std::runtime_error("Effect main input must be mono or stereo");
            if(multi){int n=component->getBusCount(kAudio,kOutput);if(n<1||n>64)throw std::runtime_error("Output bus count exceeds limit");for(int i=0;i<n;i++){BusInfo info{};if(component->getBusInfo(kAudio,kOutput,i,info)!=kResultOk||info.channelCount<0||info.channelCount>64)throw std::runtime_error("Invalid output bus info");outputs.push_back(info);}for(int bus:selected)if(bus>=n||outputs[bus].channelCount<1||outputs[bus].channelCount>2)throw std::runtime_error("Selected output must be mono/stereo");}
            for(int direction=0;direction<2;direction++)for(int i=0;i<component->getBusCount(kAudio,static_cast<BusDirection>(direction));i++){
                // Some instruments require their main bus for internal processing even when only auxiliary buses are routed.
                bool active=multi ? direction==kOutput&&(i==0||std::find(selected.begin(),selected.end(),i)!=selected.end()) : i==0&&(direction==kOutput||effect);
                auto status=component->activateBus(kAudio,static_cast<BusDirection>(direction),i,active);if(multi&&active&&status!=kResultOk)throw std::runtime_error("Output activation rejected");}
            for(int i=0;i<component->getBusCount(kEvent,kInput);i++)component->activateBus(kEvent,kInput,i,i==0);
            ProcessSetup setup{kRealtime,kSample32,maxFrames,static_cast<double>(sampleRate)};
            if(processor->setupProcessing(setup)!=kResultOk||!audio.prepare(*component,maxFrames,kSample32)||component->setActive(true)!=kResultOk)throw std::runtime_error("Audio setup failed");
            processor->setProcessing(true);running=true;
            mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,wide(mapName).c_str());if(!mapping)throw std::runtime_error("PCM shared memory is unavailable");
            view=MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,64+maxFrames*(multi?selected.size()*8:effect?16:8));if(!view||*static_cast<uint32_t*>(view)!=Magic)throw std::runtime_error("Invalid PCM shared memory");
            pcm=reinterpret_cast<float*>(static_cast<char*>(view)+64);produced=0;
            inputPcm=effect?pcm+maxFrames*2:nullptr;
            out.str(c.name());out.put<uint32_t>(processor->getLatencySamples());int n=controller->getParameterCount();if(n<0||n>4096)throw std::runtime_error("Too many plugin parameters");out.put<int32_t>(n);
            for(int i=0;i<n;i++){ParameterInfo info{};if(controller->getParameterInfo(i,info)!=kResultOk)throw std::runtime_error("Parameter lookup failed");out.put<uint32_t>(info.id);out.str(text(info.title));out.str(text(info.units));out.put<double>(controller->getParamNormalized(info.id));if(effect)out.put<int32_t>(info.stepCount);}
            if(multi){out.put<int32_t>(outputs.size());for(size_t i=0;i<outputs.size();i++){out.str(text(outputs[i].name));out.put<int32_t>(outputs[i].channelCount);out.put<int32_t>(i==0||std::find(selected.begin(),selected.end(),i)!=selected.end());}}
            found=true;break;
        }
        if(!found)throw std::runtime_error("Plugin class identity not found");
    }
    void require(){if(!running)throw std::runtime_error("No instrument loaded");}
    void run(int frames,int64_t sampleTime,double bpm,double ppq,double cycleStart,double cycleEnd,bool playing,int beatsPerBar,bool useInput=false) {
        require();if(frames<=0||frames>maxFrames||!std::isfinite(bpm)||bpm<10||bpm>60000||!std::isfinite(ppq)||beatsPerBar<1||beatsPerBar>32)throw std::runtime_error("Invalid process context");
        changes.clearQueue();for(auto p:pending){int32 index=0,point=0;auto q=changes.addParameterData(p.first,index);q->addPoint(0,p.second,point);}pending.clear();
        for(const auto& a:automation){int32 index=0,point=0;auto q=changes.addParameterData(a.id,index);if(!q)throw std::runtime_error("Too many automated parameters");q->addPoint(a.offset,a.value,point);}automation.clear();
        context={};context.state=ProcessContext::kTempoValid|ProcessContext::kProjectTimeMusicValid|ProcessContext::kTimeSigValid|ProcessContext::kCycleValid|ProcessContext::kContTimeValid|(playing?ProcessContext::kPlaying:0);
        context.sampleRate=sampleRate;context.projectTimeSamples=sampleTime;context.continousTimeSamples=sampleTime;context.projectTimeMusic=ppq;context.tempo=bpm;context.cycleStartMusic=cycleStart;context.cycleEndMusic=cycleEnd;context.timeSigNumerator=beatsPerBar;context.timeSigDenominator=4;
        audio.numSamples=frames;audio.processMode=kRealtime;audio.inputEvents=&events;audio.inputParameterChanges=&changes;audio.processContext=&context;
        for(int i=0;i<audio.numInputs;i++){audio.inputs[i].silenceFlags=~uint64(0);for(int ch=0;ch<audio.inputs[i].numChannels;ch++)std::fill_n(audio.inputs[i].channelBuffers32[ch],frames,0.f);}
        if(useInput){
            if(!effect||!inputPcm||audio.numInputs<1)throw std::runtime_error("No effect input loaded");
            auto& input=audio.inputs[0];input.silenceFlags=0;
            for(int i=0;i<frames;i++){
                float l=inputPcm[2*i],r=inputPcm[2*i+1];if(!std::isfinite(l)||!std::isfinite(r))throw std::runtime_error("Non-finite effect input");
                input.channelBuffers32[0][i]=input.numChannels==1?(l+r)*.5f:l;
                if(input.numChannels>1)input.channelBuffers32[1][i]=r;
            }
        }
        for(int i=0;i<audio.numOutputs;i++){audio.outputs[i].silenceFlags=0;for(int ch=0;ch<audio.outputs[i].numChannels;ch++)std::fill_n(audio.outputs[i].channelBuffers32[ch],frames,0.f);}
        if(multi){if(component->getBusCount(kAudio,kOutput)!=static_cast<int32>(outputs.size()))throw std::runtime_error("Output layout changed");for(int bus:selected){BusInfo info{};if(component->getBusInfo(kAudio,kOutput,bus,info)!=kResultOk||info.channelCount!=outputs[bus].channelCount||audio.outputs[bus].numChannels!=info.channelCount)throw std::runtime_error("Output layout changed");}}
        if(processor->process(audio)!=kResultOk)throw std::runtime_error("Plugin process failed");
        if(multi){for(size_t slot=0;slot<selected.size();slot++){auto& bus=audio.outputs[selected[slot]];float* destination=pcm+slot*maxFrames*2;for(int i=0;i<frames;i++){float l=bus.silenceFlags&1?0:bus.channelBuffers32[0][i],r=bus.silenceFlags&(bus.numChannels>1?2:1)?0:bus.channelBuffers32[bus.numChannels>1?1:0][i];if(!std::isfinite(l)||!std::isfinite(r))throw std::runtime_error("Non-finite instrument output");destination[2*i]=l;destination[2*i+1]=r;}}}
        else for(int i=0;i<frames;i++){float l=audio.outputs[0].channelBuffers32[0][i],r=audio.outputs[0].channelBuffers32[audio.outputs[0].numChannels>1?1:0][i];if(useInput&&(!std::isfinite(l)||!std::isfinite(r)))throw std::runtime_error("Non-finite effect output");pcm[2*i]=std::isfinite(l)?l:0;pcm[2*i+1]=std::isfinite(r)?r:0;}
        produced+=frames;MemoryBarrier();*reinterpret_cast<int64_t*>(static_cast<char*>(view)+8)=produced;*reinterpret_cast<int32_t*>(static_cast<char*>(view)+16)=frames;
    }
    void configure(Bytes& r) {
        require();auto comp=r.blob(),ctrl=r.blob();int n=r.read<int32_t>();if(n<0||n>4096)throw std::runtime_error("Invalid parameter count");
        if(!comp.empty()){MemoryStream stream(comp.data(),comp.size());if(component->setState(&stream)!=kResultOk)throw std::runtime_error("Component state restore failed");stream.seek(0,IBStream::kIBSeekSet,nullptr);controller->setComponentState(&stream);}
        if(!ctrl.empty()){MemoryStream stream(ctrl.data(),ctrl.size());if(controller->setState(&stream)!=kResultOk)throw std::runtime_error("Controller state restore failed");}
        for(int i=0;i<n;i++){auto id=r.read<uint32_t>();double value=r.read<double>();if(!std::isfinite(value)||value<0||value>1)throw std::runtime_error("Invalid normalized parameter");if(controller->setParamNormalized(id,value)!=kResultOk)throw std::runtime_error("Parameter update rejected");pending.emplace_back(id,value);}
        int warm=r.read<int32_t>();r.end();if(warm){events.clear();run(std::min(maxFrames,512),0,120,0,0,4,false,4);}produced=0;
    }
    void state(Bytes& out){require();MemoryStream comp,ctrl;if(component->getState(&comp)!=kResultOk)throw std::runtime_error("Component state capture failed");controller->getState(&ctrl);out.blob(comp.getData(),comp.getSize());out.blob(ctrl.getData(),ctrl.getSize());}
    void render(Bytes& r,Bytes& out,bool automated,bool useInput=false){
        int frames=r.read<int32_t>();auto time=r.read<int64_t>();double bpm=r.read<double>(),ppq=r.read<double>(),start=r.read<double>(),end=r.read<double>();bool playing=r.read<int32_t>()!=0;int meter=r.read<int32_t>(),n=r.read<int32_t>();
        if(n<0||n>65536)throw std::runtime_error("Too many MIDI events");events.clear();
        for(int i=0;i<n;i++){int offset=r.read<int32_t>(),pitch=r.read<int32_t>();float velocity=r.read<float>();int id=r.read<int32_t>();bool on=r.read<int32_t>()!=0;
            if(offset<0||offset>=frames||pitch<0||pitch>127||!std::isfinite(velocity)||velocity<0||velocity>1)throw std::runtime_error("Invalid MIDI event");
            Event e{};e.busIndex=0;e.sampleOffset=offset;e.ppqPosition=ppq+offset*bpm/(60*sampleRate);e.type=on?Event::kNoteOnEvent:Event::kNoteOffEvent;
            if(on){e.noteOn.channel=0;e.noteOn.pitch=static_cast<int16>(pitch);e.noteOn.velocity=velocity;e.noteOn.noteId=id;}else{e.noteOff.channel=0;e.noteOff.pitch=static_cast<int16>(pitch);e.noteOff.velocity=velocity;e.noteOff.noteId=id;}events.addEvent(e);
        }
        automation.clear();
        if(automated){
            int points=r.read<int32_t>();if(points<0||points>65536)throw std::runtime_error("Too many automation points");
            for(int i=0;i<points;i++){auto id=r.read<uint32_t>();int offset=r.read<int32_t>();double value=r.read<double>();
                if(offset<0||offset>=frames||!std::isfinite(value)||value<0||value>1)throw std::runtime_error("Invalid automation point");
                if(!automation.empty()&&(automation.back().id>id||(automation.back().id==id&&automation.back().offset>offset)))throw std::runtime_error("Automation points must be sorted by parameter and offset");
                automation.push_back({id,offset,value});}
        }
        r.end();run(frames,time,bpm,ppq,start,end,playing,meter,useInput);out.put<int32_t>(frames);
        if(useInput||multi)out.put<uint32_t>(processor->getLatencySamples());
    }
};
}
int main(int argc,char** argv){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    _setmode(0,_O_BINARY);_setmode(1,_O_BINARY);replyFd=_dup(1);_dup2(2,1); // plugin stdout goes to stderr, never into the protocol
    if(argc==2){DWORD parent=static_cast<DWORD>(std::stoul(argv[1]));std::thread([parent]{HANDLE h=OpenProcess(SYNCHRONIZE,FALSE,parent);if(!h)_exit(0);WaitForSingleObject(h,INFINITE);CloseHandle(h);_exit(0);}).detach();}
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);auto app=owned(new HostApplication());PluginContextFactory::instance().setPluginContext(app);Processor plugin;
    try{for(;;){Bytes header;header.data.resize(12);if(!receive(header.data.data(),12))break;auto magic=header.read<uint32_t>();auto version=header.read<uint16_t>(),op=header.read<uint16_t>();auto size=header.read<uint32_t>();if(magic!=Magic||version!=1||size>MaxMessage)break;
        Bytes request;request.data.resize(size);if(!receive(request.data.data(),size))break;Bytes reply;reply.put<int32_t>(0);
        try{pump();switch(op){
            case 0:request.end();reply.put<int32_t>(4);break;
            case 1:{auto path=request.str();request.end();std::string error;auto module=VST3::Hosting::Module::create(path,error);if(!module)throw std::runtime_error(error);auto factory=module->getFactory();factory.setHostContext(app);auto classes=factory.classInfos();int n=0;for(const auto& c:classes)if(c.category()==kVstAudioEffectClass)n++;reply.put<int32_t>(n);for(const auto& c:classes)if(c.category()==kVstAudioEffectClass){reply.str(c.ID().toString());reply.str(c.name());reply.str(c.vendor());reply.str(c.version());reply.put<int32_t>(c.subCategoriesString().find("Instrument")!=std::string::npos);}break;}
            case 2:plugin.load(request,reply);break;case 9:plugin.load(request,reply,true);break;case 11:plugin.load(request,reply,false,true);break;case 12:if(!plugin.multi)throw std::runtime_error("No multi-output instrument");plugin.render(request,reply,true);break;case 10:plugin.render(request,reply,true,true);break;case 3:plugin.configure(request);reply.put<uint32_t>(plugin.processor->getLatencySamples());break;case 4:plugin.render(request,reply,false);break;case 8:plugin.render(request,reply,true);break;case 5:request.end();plugin.state(reply);break;case 6:request.end();plugin.close();break;case 7:request.end();plugin.close();break;default:throw std::runtime_error("Unknown operation");
        }if(reply.data.size()>MaxMessage)throw std::runtime_error("Reply exceeds protocol limit");}catch(const std::exception& e){reply.data.clear();reply.put<int32_t>(1);reply.str(e.what());}
        Bytes h;h.put<uint32_t>(Magic);h.put<uint16_t>(1);h.put<uint16_t>(op);h.put<uint32_t>(reply.data.size());send(h.data.data(),h.data.size());send(reply.data.data(),reply.data.size());if(op==7)break;
    }}catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());}
    plugin.close();PluginContextFactory::instance().setPluginContext(nullptr);app=nullptr;CoUninitialize();return 0;
}
