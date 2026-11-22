import json
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

root=Path(os.environ.get('VB_BROWSER_EVIDENCE','artifacts/browser-verify'))
root.mkdir(parents=True,exist_ok=True)
fixture=json.loads(Path('relay/artifacts/account-browser/fixture-info.json').read_text())
room=fixture['room']
origin='http://localhost:4102'
password='VoiceBridge synthetic test 2026!'
fake_capture="""
() => {
  const mic=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
  const devices=navigator.mediaDevices.enumerateDevices.bind(navigator.mediaDevices);
  navigator.mediaDevices.enumerateDevices=async()=>[
    ...await devices(),
    {kind:'videoinput',deviceId:'test-one',label:'合成摄像头 A'},
    {kind:'videoinput',deviceId:'test-two',label:'合成摄像头 B'}
  ];
  navigator.mediaDevices.getUserMedia=options=>{
    if(options.video){
      if(window.rejectNextCamera){window.rejectNextCamera=false;return Promise.reject(new DOMException('Synthetic denied','NotAllowedError'));}
      return Promise.resolve(window.fixtureVideo(options.video.deviceId?.exact||'合成摄像头','#28425f'));
    }
    return mic(options);
  };
}
"""
report={}
with sync_playwright() as playwright:
    browser=playwright.chromium.launch(channel=os.environ.get('VB_BROWSER_CHANNEL') or ('msedge' if os.name=='nt' else None),headless=True,args=['--disable-features=WebRtcHideLocalIpsWithMdns'])
    errors=[]
    def page(context):
        item=context.new_page()
        if os.environ.get('VB_REQUIRE_RELAY'):
            item.add_init_script('''(() => {
              const Native=window.RTCPeerConnection;
              window.RTCPeerConnection=class extends Native {
                constructor(config){super({...config,iceTransportPolicy:'relay'});}
              };
            })();''')
        item.on('pageerror',lambda error: errors.append(str(error)))
        return item
    for width in (390,768,1440):
        landing=page(browser.new_context(viewport={'width':width,'height':880},reduced_motion='reduce' if width==390 else 'no-preference'))
        landing.goto(origin,wait_until='networkidle')
        expect(landing.get_by_role('heading',name='VoiceBridge')).to_be_visible()
        assert landing.evaluate('document.documentElement.scrollWidth===innerWidth')
        if width!=390:
            expect(landing.locator('.techTextReady')).to_be_visible()
        landing.screenshot(path=str(root/f'home-{width}.png'),full_page=True)
        landing.context.close()
    report['homeWidths']=[390,768,1440]

    def fresh_member(index,deny=False):
        context=browser.new_context(viewport={'width':1440,'height':900})
        p=page(context)
        p.goto(room)
        p.wait_for_url('**/login')
        p.locator('#registerTab').click()
        address=f'release140.{index}@example.com'
        p.locator('#email').fill(address)
        p.locator('#sendCode').click()
        expect(p.locator('#authError')).to_contain_text('验证码已发送')
        code=context.request.get(origin+'/__fixture-mail',headers={'X-Test-Address':address}).json()['code']
        p.locator('#code').fill(code)
        p.locator('#submitAuth').click()
        expect(p.locator('#confirmPassword')).to_be_visible()
        p.locator('#password').fill('开黑验证12')
        p.locator('#confirmPassword').fill('开黑验证12')
        p.locator('#nickname').fill(f'新成员{index}')
        p.locator('#submitAuth').click()
        p.wait_for_url(room)
        p.evaluate(fake_capture)
        if deny:
            p.evaluate("""() => {
              const capture=navigator.mediaDevices.getUserMedia;
              let first=true;
              navigator.mediaDevices.getUserMedia=options=>{
                if(options.audio&&first){first=false;return Promise.reject(new DOMException('Synthetic denied','NotAllowedError'));}
                return capture(options);
              };
            }""")
        p.locator('#join').click()
        expect(p.locator('#connectionBadge')).to_have_text('已连接')
        if deny:
            expect(p.locator('#status')).to_contain_text('权限未开启')
            p.locator('#microphone').click()
        expect(p.locator('#microphone')).to_have_attribute('aria-checked','true')
        return p

    # Place an already registered user in the screen slot to fit the fixture's isolated mail quota.
    def viewer():
        context=browser.new_context(viewport={'width':1440,'height':900})
        p=page(context)
        p.goto(origin+'/login')
        p.locator('#email').fill('player.one@example.com')
        p.locator('#password').fill(password)
        p.locator('#submitAuth').click()
        p.wait_for_url(origin+'/')
        p.goto(room)
        p.evaluate(fake_capture)
        p.locator('#join').click()
        expect(p.locator('#connectionBadge')).to_have_text('已连接')
        expect(p.locator('#microphone')).to_have_attribute('aria-checked','true')
        return p

    a=fresh_member(1,True)
    b=fresh_member(2)
    screen=viewer()
    expect(a.locator('#onlineCount')).to_have_text('4 / 8 人')
    duplicate=page(a.context)
    duplicate.goto(room)
    duplicate.locator('#join').click()
    expect(duplicate.locator('#status')).to_contain_text('此账号已在')
    expect(a.locator('#connectionBadge')).to_have_text('已连接')
    duplicate.close()
    report['newRegistrationMicrophoneAndDuplicate']=True

    screen.locator('#micGain').evaluate("""el=>{
      for(let i=0;i<450;i++){
        el.value=i%401;
        el.dispatchEvent(new Event('input',{bubbles:true}));
      }
      el.value=175;el.dispatchEvent(new Event('input',{bubbles:true}));
      el.dispatchEvent(new Event('change',{bubbles:true}));
    }""")
    expect(screen.locator('#micGainValue')).to_have_text('175%')
    screen.wait_for_timeout(1800)
    expect(screen.locator('#connectionBadge')).to_have_text('已连接')
    report['rapidVolumeChanges']=450

    a.evaluate('window.rejectNextCamera=true')
    a.locator('#cameraStart').click()
    expect(a.locator('#cameraStatus')).to_contain_text('摄像头未获授权')
    expect(a.locator('#microphone')).to_have_attribute('aria-checked','true')
    a.locator('#cameraStart').click()
    b.locator('#cameraStart').click()
    screen.locator('#screenStart').click()
    expect(a.locator('#cameraPreview')).to_be_visible(timeout=9000)
    expect(b.locator('#cameraPreview')).to_be_visible(timeout=9000)
    expect(b.locator('#cameraDevice option[value=test-two]')).to_have_count(1)
    b.evaluate('window.previousCamera=document.querySelector("#cameraPreview").srcObject.getVideoTracks()[0]')
    b.locator('#cameraDevice').select_option('test-two')
    b.wait_for_function('()=>window.previousCamera.readyState==="ended"')
    expect(b.locator('#cameraPreview')).to_be_visible()
    expect(screen.locator('#screenVideo')).to_be_visible(timeout=9000)
    a.wait_for_function('()=>document.querySelector("#screenVideo").videoWidth>0',timeout=25000)
    screen.wait_for_function('()=>[...document.querySelectorAll(".cameraTile video")].filter(v=>v.videoWidth>0).length===2',timeout=25000)
    screen.screenshot(path=str(root/'room-1440.png'),full_page=True)
    for width in (768,390):
        screen.set_viewport_size({'width':width,'height':850})
        screen.screenshot(path=str(root/f'room-{width}.png'),full_page=True)
        assert screen.evaluate('document.documentElement.scrollWidth===innerWidth')
    report['concurrentCameraAndDisplay']={'cameras':2,'displays':1,'transport':'TURN relay only' if os.environ.get('VB_REQUIRE_RELAY') else 'local WebRTC'}

    screen.locator('#cameraStart').click()
    expect(screen.locator('#cameraPreview')).to_be_visible(timeout=12000)
    expect(a.locator('#screenVideo')).to_be_hidden()
    screen.locator('#cameraStart').click()
    expect(screen.locator('#cameraPreview')).to_be_hidden()
    expect(screen.locator('#connectionBadge')).to_have_text('已连接')
    report['atomicCameraSwitch']=True
    assert screen.context.request.post(origin+'/__fill').ok
    expect(screen.locator('#onlineCount')).to_have_text('8 / 8 人')
    before=screen.context.request.get(origin+'/__stats').json()['testMembers'][0]['id']
    assert screen.context.request.post(origin+'/__drop').ok
    screen.wait_for_function('()=>document.querySelector("#connectionBadge").textContent==="已连接"')
    after=screen.context.request.get(origin+'/__stats').json()['testMembers'][0]['id']
    assert before==after
    report['fullRoomRecovery']=True
    if os.environ.get('VB_REQUIRE_RELAY'):
        expect(a.locator('#screenRelay')).to_contain_text('服务器中继')
    stats=screen.context.request.get(origin+'/__stats').json()
    assert stats['frames']>0,stats
    report['syntheticAudioFrames']=stats['frames']
    report['pageErrors']=errors
    assert not errors,errors
    browser.close()
(root/'browser-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps(report,ensure_ascii=False))
