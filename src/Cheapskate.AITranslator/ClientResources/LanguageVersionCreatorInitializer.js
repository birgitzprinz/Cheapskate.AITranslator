define([
    "dojo/_base/declare",
    "xstyle/css!cheapskate/LanguageVersionCreator.css",
    "epi/_Module",
    "epi/dependency",
    "cheapskate/commands/LanguageVersionCreatorToolbarProvider"
], function (
    declare,
    css,
    _Module,
    dependency,
    LanguageVersionCreatorToolbarProvider
) {
    return declare([_Module], {
        initialize: function () {
            this.inherited(arguments);

            var commandregistry = dependency.resolve("epi.globalcommandregistry");
            if (commandregistry && commandregistry.registerProvider) {
                commandregistry.registerProvider("epi.cms.globalToolbar", new LanguageVersionCreatorToolbarProvider());
            }
        }
    });
});
