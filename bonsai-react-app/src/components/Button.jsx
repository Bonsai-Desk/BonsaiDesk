import React from 'react';

import {postJson} from '../utilities';
import {smallRoundButtonClass, smallRoundButtonClassGreen} from '../cssClasses';
import ForwardImg from '../static/forward.svg';
import BackImg from '../static/back.svg';

function postMouseDown() {
    postJson({Type: 'event', Message: 'mouseDown'});
}

function postMouseUp() {
    postJson({Type: 'event', Message: 'mouseUp'});
}

function postHover() {
    postJson({Type: 'event', Message: 'hover'});
}

export function ToggleButton(props) {
    let {
        handleClick,
        classDisabled = '',
        classEnabled = '',
        shouldPostDown = true,
        shouldPostHover = true,
        shouldPostUp = true,
        enabled = false,
    } = props;
    return (
            <div onPointerEnter={shouldPostHover ? postHover : null}
                 onPointerDown={() => {
                     handleClick();
                     if (shouldPostDown) {
                         postMouseDown();
                     }
                 }}
                 onPointerUp={shouldPostUp ? postMouseUp : null}
            >
                <div className={enabled ? classEnabled : classDisabled}>
                    {props.children}
                </div>
            </div>
    );
}

export function NormalButton(props) {
    let {onClick, children} = props;
    return <Button {...props} handleClick={onClick}>{children}</Button>;
}

export function InstantButton(props) {
    let {onClick, children} = props;
    return <Button {...props} handleClick={onClick}>{children}</Button>;
}

function Button(props) {
    let {
        handleClick,
        className = '',
        shouldPostDown = true,
        shouldPostHover = true,
        shouldPostUp = true,
    } = props;
    return (
            <div className={className} onPointerEnter={shouldPostHover ? postHover : null}
                 onPointerDown={() => {
                     handleClick();
                     if (shouldPostDown) {
                         postMouseDown();
                     }
                 }}
                 onPointerUp={shouldPostUp ? postMouseUp : null}
            >
                    {props.children}
            </div>
    );
}

export function ForwardButton({onClick, variant}) {
    let className = smallRoundButtonClass;
    if (variant === "green"){
        className = smallRoundButtonClassGreen
    }
    return <Button className={className} handleClick={onClick}>
        <div className={'flex flex-wrap content-center justify-center'}>
            <img className={'h-8 object-contain'} src={ForwardImg} alt={'forward'}/>
        </div>
    </Button>;
}

export function BackButton({onClick}) {
    return <Button className={smallRoundButtonClass} handleClick={onClick}>
        <div className={'flex flex-wrap content-center justify-center'}>
            <img className={'h-8 object-contain'} src={BackImg} alt={'back'}/>
        </div>
    </Button>;
}

export function UpButton(props) {
    let {
        handleClick,
        className = '',
        shouldPostDown = true,
        shouldPostHover = true,
        shouldPostUp = true,
    } = props;
    return (
            <div onPointerEnter={shouldPostHover ? postHover : null}
                 onPointerDown={() => {
                     if (shouldPostDown) {
                         postMouseDown();
                     }
                 }}
                 onPointerUp={() => {
                     handleClick();
                     if (shouldPostUp) {
                         postMouseUp();
                     }
                 }}
            >
                <div className={className}>
                    {props.children}
                </div>
            </div>
    );
}

