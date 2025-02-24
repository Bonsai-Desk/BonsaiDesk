import React, {useEffect, useState} from 'react';
import {Route, Switch, useHistory, useRouteMatch} from 'react-router-dom';
import axios from 'axios';
import {observer} from 'mobx-react-lite';
import {action, autorun} from 'mobx';

import DotsImg from '../static/dots-vertical.svg';
import {NetworkManagerMode, useStore} from '../DataProvider';
import {postPublicRoomAvailable, postRequestMicrophone} from '../api';

import {apiBase} from '../utilities';
import {InstantButton} from '../components/Button';
import './Menu.css';
import {DebugPage} from './Debug';
import {SettingsPage} from './Settings';
import {HomePage} from './Home';
import {PlayerPage} from './Player';
import PublicRoomsPage from './PublicRooms';
import {YourRoom} from './YourRoom';
import {BlocksPage} from './Blocks';

function NoMicPage() {

    const className = 'py-4 px-8 font-bold bg-gray-800 active:bg-gray-700 hover:bg-gray-600 rounded cursor-pointer flex flex-wrap content-center';

    function handleClick() {
        postRequestMicrophone();
    }

    return (
            <div className={'flex flex-wrap content-center justify-center bg-black w-full h-screen'}>
                <div className={''}>
                    <div className={'flex justify-center'}>
                        <div className={'text-2xl p-4 flex flex-wrap content-center text-white bg-red-800 rounded'}>
                            No Access to Microphone
                        </div>
                    </div>
                    <div className={'h-4'}/>
                    <div className={'flex justify-center'}>
                        <div className={'text-2xl font-normal text-white '}>
                            <InstantButton className={className} onClick={handleClick}>Request</InstantButton>
                        </div>
                    </div>
                    <div className={'h-4'}/>
                    <div className={'flex text-white'}>
                        <span>If that does not work, check your app permissions under </span>
                        <img src={DotsImg} alt={''}/>
                    </div>
                </div>
            </div>
    );

}

function NavItem(props) {
    let {
        handleClick,
        inactive = false,
        buttonClassSelected = '',
        buttonClass = '',
        buttonClassInactive = '',
        to = '',
        unread = false,
        matchAll = '',
    } = props;

    let history = useHistory();

    buttonClass = buttonClass ?
            buttonClass :
            'py-4 px-8 hover:bg-gray-800 active:bg-gray-900 hover:text-white rounded cursor-pointer flex flex-wrap content-center';
    buttonClassSelected = buttonClassSelected ?
            buttonClassSelected :
            'py-4 px-8 bg-bonsai-green text-white rounded cursor-pointer flex flex-wrap content-center';
    buttonClassInactive = buttonClassInactive ?
            buttonClassInactive :
            'py-4 px-8 bg-gray-800 rounded cursor-pointer flex flex-wrap content-center';

    let matchAllPath = matchAll.split('/');
    let actualPath = window.location.pathname.split('/');
    let selected = false;

    if (matchAll) {
        let matched = false;
        for (let i = 0; i < matchAllPath.length; i++) {
            matched = (matchAllPath[i] === actualPath[i]);
        }
        if (matched) {
            selected = true;
        }
    } else {
        selected = window.location.pathname === to;
    }

    let textClass = selected ? 'text-white' : 'text-gray-300';

    if (inactive) {
        return (
                <div className={buttonClassInactive}>
                    {props.children}
                </div>
        );
    }

    let className = selected ? buttonClassSelected : buttonClass;

    if (to && !matchAll) {
        className = window.location.pathname === to ? buttonClassSelected : buttonClass;
    }

    if (to) {
        return (
                <InstantButton className={className} onClick={() => {
                    history.push(to);
                }}>
                    <div className={'w-full flex flex-wrap justify-between content-center'}>
                        <span className={textClass}>{props.children}</span>
                        {unread ? <div className={'mt-2 w-3 h-3 bg-gray-200 rounded-full'}/> : ''}
                    </div>
                </InstantButton>
        );

    }

    return (
            <InstantButton className={className} onClick={handleClick}>
                {props.children}
            </InstantButton>
    );
}

function NavList(props) {
    return (
            <div className={'space-y-1 px-2'}>
                {props.children}
            </div>);

}

function NavTitle(props) {
    return <div
            className={'text-white font-bold text-xl px-5 pt-5 pb-2'}>{props.children}</div>;
}

let Menu = observer(() => {
    let {store, mediaInfo} = useStore();
    let [roomsCount, setRoomsCount] = useState(-1);
    let [oldRoomsCount, setOldRoomsCount] = useState(-1);

    let debug = store.AppInfo.Build === 'DEVELOPMENT';

    let match = useRouteMatch();

    document.title = 'Menu';

    useEffect(() => {
        if (oldRoomsCount === 0 && roomsCount > 0) {
            postPublicRoomAvailable();
        }
        setOldRoomsCount(roomsCount);
    }, [roomsCount, oldRoomsCount]);

    useEffect(() => {
        autorun(() => {
            // remove room code if
            // DEVELOPMENT || PRODUCTION
            const networkAddress = store.NetworkInfo.NetworkAddress;
            const roomOpen = store.NetworkInfo.RoomOpen;
            const roomCode = store.RoomCode;
            const loadingRoomCode = store.LoadingRoomCode;
            const userName = store.SocialInfo.UserName;
            const version = `${store.AppInfo.Version}b${store.AppInfo.BuildId}`;
            const publicRoom = store.NetworkInfo.PublicRoom ? 1 : 0;

            let setLoadingRoomCode = action((value) => {
                store.LoadingRoomCode = value;
            });

            let setRoomSecret = action((value) => {
                store.RoomSecret = value;
            });

            if (roomCode && (!networkAddress || !roomOpen)) {
                store.RoomCode = null;
                return;
            }

            // send ip/port out for a room code
            if (roomOpen && !roomCode && !loadingRoomCode && networkAddress) {
                setLoadingRoomCode(true);
                let url = apiBase(store) + '/rooms';
                let data = `network_address=${networkAddress}&username=${userName}&version=${version}&public_room=${publicRoom}`;
                axios(
                        {
                            method: 'post',
                            url: url,
                            data: data,
                            header: {'content-type': 'application/x-www-form-urlencoded'},
                        },
                ).then(response => {
                    let tag = response.data.tag;
                    let secret = response.data.secret;

                    setRoomSecret(secret);
                    store.RoomCode = tag;
                    setLoadingRoomCode(false);
                }).catch(err => {
                    console.log(err);
                    setLoadingRoomCode(false);
                });
            }
        });

    });

    useEffect(() => {
        return () => {
            store.RoomCode = null;
        };
    }, [store]);

    useEffect(() => {
        let url = store.ApiBase + '/rooms_info';
        let query = setInterval(() => {
            axios({
                method: 'GET',
                url: url,
            }).then(resp => {
                setRoomsCount(resp.data.count);
            }).catch(console.log);
        }, 2500);
        return () => {
            clearInterval(query);
        };
    }, [store.ApiBase]);

    if (!store.AppInfo.MicrophonePermission) {
        return <NoMicPage/>;
    }

    let mediaButtonClass = '';
    let mediaButtonClassSelected = '';

    if (mediaInfo.Active) {
        mediaButtonClass = 'py-4 px-8 hover:bg-gray-800 active:bg-gray-900 hover:text-white rounded cursor-pointer flex flex-wrap content-center';
        mediaButtonClassSelected = 'py-4 px-8 bg-bonsai-green text-white rounded cursor-pointer flex flex-wrap content-center';
    }

    let homeActive = store.NetworkInfo.RoomOpen || store.NetworkInfo.Mode === NetworkManagerMode.ClientOnly;

    return (
            <div className={'flex text-lg text-gray-500 h-full static'}>
                {!store.NetworkInfo.Online ?
                        <div className={'text-2xl p-4 flex flex-wrap content-center absolute text-white bg-red-800 bottom-2 right-2 z-20 rounded'}>
                            Internet Error: Check Your Connection
                        </div>
                        : ''

                }
                <div className={'w-4/12 bg-black overflow-auto scroll-host static'}>
                    <div className={'w-4/12 bg-black fixed'}>
                        <NavTitle>
                            Menu
                        </NavTitle>
                    </div>

                    <div className={'h-16'}/>
                    <NavList>
                        <NavItem to={'/menu/home'} matchAll={'/menu/home'} unread={homeActive}>Home</NavItem>
                        <NavItem to={'/menu/public-rooms'}>Public Rooms</NavItem>
                        <NavItem to={'/menu/blocks/hot'} matchAll={'/menu/blocks'}>Builds</NavItem>
                        <NavItem to={'/menu/player'}
                                 buttonClass={mediaButtonClass}
                                 buttonClassSelected={mediaButtonClassSelected}
                                 unread={mediaInfo.Active}
                        >

                            Media
                        </NavItem>
                        <NavItem to={'/menu/room'}>Lights & Layout</NavItem>
                        <NavItem to={'/menu/settings'} matchAll={'/menu/settings'}>Settings</NavItem>
                        {debug ?
                                <NavItem to={'/menu/debug'} component={DebugPage}>Debug</NavItem>
                                : ''
                        }
                    </NavList>
                </div>

                <div className={'bg-gray-900 z-10 w-full overflow-auto scroll-host'}>
                    <Switch>
                        <Route path={`${match.path}/home`} component={HomePage}/>
                        <Route path={`${match.path}/room`} component={YourRoom}/>
                        <Route path={`${match.path}/settings`} component={SettingsPage}/>
                        <Route path={`${match.path}/debug`} component={DebugPage}/>
                        <Route path={`${match.path}/blocks`} component={BlocksPage}/>
                        <Route path={`${match.path}/player`} component={PlayerPage}/>
                        <Route path={`${match.path}/public-rooms`} component={PublicRoomsPage}/>
                        <Route path={`${match.path}`}>Page not found</Route>
                    </Switch>
                </div>

            </div>
    );
});

export default Menu;
