﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging;

using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

using uSync.Core.Models;

using static Umbraco.Cms.Core.Constants;
using static Umbraco.Cms.Core.Constants.Conventions;

namespace uSync.Core.Serialization.Serializers
{
    [SyncSerializer("D0E0769D-CCAE-47B4-AD34-4182C587B08A", "Template Serializer", uSyncConstants.Serialization.Template)]
    public class TemplateSerializer : SyncSerializerBase<ITemplate>, ISyncSerializer<ITemplate>
    {
        private readonly IFileService fileService;
        private readonly IShortStringHelper shortStringHelper;
        private readonly IFileSystem _viewFileSystem;

        public TemplateSerializer(IEntityService entityService, ILogger<TemplateSerializer> logger,
            IShortStringHelper shortStringHelper,
            IFileService fileService,
            FileSystems fileSystems)
            : base(entityService, logger)
        {
            this.fileService = fileService;
            this.shortStringHelper = shortStringHelper;

            _viewFileSystem = fileSystems.MvcViewsFileSystem;
        }

        protected override SyncAttempt<ITemplate> DeserializeCore(XElement node, SyncSerializerOptions options)
        {
            var key = node.GetKey();
            var alias = node.GetAlias();

            var name = node.Element("Name").ValueOrDefault(string.Empty);
            var item = default(ITemplate);

            var details = new List<uSyncChange>();

            if (key != Guid.Empty)
                item = fileService.GetTemplate(key);

            if (item == null)
                item = fileService.GetTemplate(alias);

            if (item == null)
            {
                item = new Template(shortStringHelper, name, alias);
                details.AddNew(alias, alias, "Template");

                if (ShouldGetContentFromNode(node, options))
                {
                    logger.LogDebug("Getting content for Template from XML");
                    item.Content = GetContentFromConfig(node);
                }
                else
                {
                    logger.LogDebug("Loading template content from disk");

                    var templatePath = ViewPath(alias);
                    if (_viewFileSystem.FileExists(templatePath))
                    {
                        logger.LogDebug("Reading {0} contents", templatePath);
                        item.Content = GetContentFromFile(templatePath);
                        item.Path = templatePath;
                    }
                    else
                    {
                        if (!options.GetSetting<bool>("UsingRazorViews", false))
                        {
                            // template is missing
                            // we can't create 
                            logger.LogWarning("Failed to create template {path} the local file is missing", templatePath);
                            return SyncAttempt<ITemplate>.Fail(name, ChangeType.Import, $"The template {templatePath} file is missing.");
                        }
                        else
                        {
                            // template is not on disk, we could use the viewEngine to find the view 
                            // if this finds the view it tells us that the view is somewhere else ? 

                            logger.LogDebug("Failed to find content, but UsingRazorViews so will create anyway, then delete the file");
                            item.Content = $"<!-- [uSyncMarker:{this.Id}]  template content - will be removed -->";
                        }
                    }
                }
            }

            if (item == null)
            {
                // creating went wrong
                logger.LogWarning("Failed to create template");
                return SyncAttempt<ITemplate>.Fail(name, ChangeType.Import, "Failed to create template");
            }

            if (item.Key != key)
            {
                details.AddUpdate(uSyncConstants.Xml.Key, item.Key, key);
                item.Key = key;
            }

            if (item.Name != name)
            {
                details.AddUpdate(uSyncConstants.Xml.Name, item.Name, name);
                item.Name = name;
            }

            if (item.Alias != alias)
            {
                details.AddUpdate(uSyncConstants.Xml.Alias, item.Alias, alias);
                item.Alias = alias;
            }

            if (ShouldGetContentFromNode(node, options))
            {
                var content = GetContentFromConfig(node);
                if (content != item.Content)
                {
                    details.AddUpdate("Content", item.Content, content);
                    item.Content = content;
                }
            }

            //var master = node.Element("Parent").ValueOrDefault(string.Empty);
            //if (master != string.Empty)
            //{
            //    var masterItem = fileService.GetTemplate(master);
            //    if (masterItem != null)
            //        item.SetMasterTemplate(masterItem);
            //}

            // Deserialize now takes care of the save.
            // fileService.SaveTemplate(item);

            return SyncAttempt<ITemplate>.Succeed(item.Name, item, ChangeType.Import, details);
        }

        /// <summary>
        ///  As a default if the file contains the content node, then we are going to use it 
        ///  for the content. if it doesn't then we are not.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        private bool ShouldGetContentFromNode(XElement node, SyncSerializerOptions options)
            => node.Element("Contents") != null; // && options.GetSetting(uSyncConstants.Conventions.IncludeContent, false);

        public string GetContentFromConfig(XElement node)
            => node.Element("Contents").ValueOrDefault(string.Empty);

        public string GetContentFromFile(string templatePath)
        {
            var content = "";
            using (var stream = _viewFileSystem.OpenFile(templatePath))
            {
                using (var sr = new StreamReader(stream))
                {
                    content = sr.ReadToEnd();
                    sr.Close();
                    sr.Dispose();
                }

                stream.Close();
                stream.Dispose();
            }

            return content;
        }

        public override SyncAttempt<ITemplate> DeserializeSecondPass(ITemplate item, XElement node, SyncSerializerOptions options)
        {
            var details = new List<uSyncChange>();
            var saved = false;
            var msg = "";

            var master = node.Element("Parent").ValueOrDefault(string.Empty);
            if (master != string.Empty && item.MasterTemplateAlias != master)
            {
                logger.LogDebug("Looking for master {0}", master);
                var masterItem = fileService.GetTemplate(master);
                if (masterItem != null && item.MasterTemplateAlias != master)
                {
                    details.AddUpdate("Parent", item.MasterTemplateAlias, master);

                    logger.LogDebug("Setting Master {0}", masterItem.Alias);
                    item.SetMasterTemplate(masterItem);

                    SaveItem(item);
                    saved = true;
                }
            }

            if (options.GetSetting("UsingRazorViews", false))
            {
                // using razor views - we delete the template file at the end (because its in a razor view). 
                var templatePath = ViewPath(item.Alias);
                if (_viewFileSystem.FileExists(templatePath))
                {
                    var fullPath = _viewFileSystem.GetFullPath(templatePath);

                    if (System.IO.File.Exists(fullPath))
                    {
                        var content = System.IO.File.ReadAllText(fullPath);
                        if (content.Contains($"[uSyncMarker:{this.Id}]"))
                        {
                            logger.LogDebug($"Removing the file from disk, because it exists in a razor view {templatePath}");
                            msg = "Using Razor views";
                            _viewFileSystem.DeleteFile(templatePath);

                            // we have to tell the handlers we saved it - or they will and write the file back 
                            return SyncAttempt<ITemplate>.Succeed(item.Name, item, ChangeType.Import, "Razor view removed", true, details);
                        }
                    }
                }
            }

            return SyncAttempt<ITemplate>.Succeed(item.Name, item, ChangeType.Import, msg, saved, details);
        }


        protected override SyncAttempt<XElement> SerializeCore(ITemplate item, SyncSerializerOptions options)
        {
            var node = this.InitializeBaseNode(item, item.Alias, this.CalculateLevel(item));

            node.Add(new XElement("Name", item.Name));
            node.Add(new XElement("Parent", item.MasterTemplateAlias));

            if (options.GetSetting(uSyncConstants.Conventions.IncludeContent, false))
            {
                node.Add(SerializeContent(item));
            }

            return SyncAttempt<XElement>.Succeed(item.Name, node, typeof(ITemplate), ChangeType.Export);
        }

        private XElement SerializeContent(ITemplate item)
        {
            return new XElement("Contents", new XCData(item.Content));
        }

        private int CalculateLevel(ITemplate item)
        {
            if (item.MasterTemplateAlias.IsNullOrWhiteSpace()) return 1;

            int level = 1;
            var current = item;
            while (!string.IsNullOrWhiteSpace(current.MasterTemplateAlias) && level < 20)
            {
                level++;
                var parent = fileService.GetTemplate(current.MasterTemplateAlias);
                if (parent == null) return level;

                current = parent;
            }

            return level;
        }

        public override ITemplate FindItem(int id)
            => fileService.GetTemplate(id);

        public override ITemplate FindItem(string alias)
            => fileService.GetTemplate(alias);

        public override ITemplate FindItem(Guid key)
            => fileService.GetTemplate(key);

        public override void SaveItem(ITemplate item)
            => fileService.SaveTemplate(item);

        public override void Save(IEnumerable<ITemplate> items)
            => fileService.SaveTemplate(items);

        public override void DeleteItem(ITemplate item)
            => fileService.DeleteTemplate(item.Alias);

        public override string ItemAlias(ITemplate item)
            => item.Alias;

        /// <summary>
        ///  we clean the content out of the template,
        ///  We don't care if the content has changed during a normal serialization
        /// </summary>
        protected override XElement CleanseNode(XElement node)
        {
            var contentNode = node.Element("Content");
            if (contentNode != null) contentNode.Remove();

            return base.CleanseNode(node);
        }


        private string ViewPath(string alias)
            => _viewFileSystem.GetRelativePath(alias.Replace(" ", "") + ".cshtml");
    }
}
