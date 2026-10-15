define([
    "dojo/_base/declare",
    "dijit/form/Button",
    "epi-cms/component/command/_GlobalToolbarCommandProvider",
    "cheapskate/commands/CreateLanguageVersionsCommand"
], function (
    declare,
    Button,
    _GlobalToolbarCommandProvider,
    CreateLanguageVersionsCommand
) {
    return declare([_GlobalToolbarCommandProvider], {
        constructor: function () {
            this.inherited(arguments);

            this.addToTrailing(new CreateLanguageVersionsCommand(), {
                showLabel: false,
                widget: Button
            });
        }
    });
});
